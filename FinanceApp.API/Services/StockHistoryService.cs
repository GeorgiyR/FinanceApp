using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public class StockHistoryService : IStockHistoryService
{
    private const int MaxYahooRequestAttempts = 5;
    private const int FullBackfillMinimumLookbackDays = 366 * 5;
    private static readonly TimeSpan YahooRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan YahooRetryMaxDelay = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> StockRefreshLocks = new();
    private static readonly ConcurrentDictionary<int, DateTime> LastOnDemandIntradayRefreshAttemptUtc = new();
    private static readonly Regex ProviderSymbolRegex = new(@"^[A-Za-z0-9\^\.\-_]{1,50}$", RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;
    private readonly IYahooRequestCoordinator _yahooRequestCoordinator;
    private readonly IStockQuoteConversionService _stockQuoteConversionService;
    private readonly TimeProvider _timeProvider;
    private readonly StockHistoryRefreshOptions _options;
    private readonly ILogger<StockHistoryService> _logger;

    public StockHistoryService(
        AppDbContext dbContext,
        IYahooRequestCoordinator yahooRequestCoordinator,
        IStockQuoteConversionService stockQuoteConversionService,
        TimeProvider timeProvider,
        IOptions<StockHistoryRefreshOptions> options,
        ILogger<StockHistoryService> logger)
    {
        _dbContext = dbContext;
        _yahooRequestCoordinator = yahooRequestCoordinator;
        _stockQuoteConversionService = stockQuoteConversionService;
        _timeProvider = timeProvider;
        _options = NormalizeOptions(options.Value);
        _logger = logger;
    }

    public async Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var stocks = await LoadDueAutomaticStocksAsync(nowUtc, cancellationToken);

        foreach (var stock in stocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var syncResult = await RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic, cancellationToken);
                if (syncResult.RateLimited)
                {
                    _logger.LogInformation("Stopping Yahoo history refresh cycle early because a shared cooldown is active.");
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed syncing history for stock {StockId}", stock.Id);
            }
        }
    }

    public async Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
    {
        await RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic, cancellationToken);
    }

    public async Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
    {
        return await RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Manual, cancellationToken);
    }

    public async Task<StockHistoryRefreshResponse> RefreshHistoryAsync(
        Stock stock,
        StockHistoryRefreshTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (stock is null)
        {
            throw new ArgumentNullException(nameof(stock));
        }

        if (string.IsNullOrWhiteSpace(stock.Ticker) || !StockExchanges.TryNormalize(stock.Exchange, out _))
        {
            throw new InvalidOperationException("Stock ticker and exchange must be valid before refreshing history.");
        }

        var requestedTicker = stock.Ticker.Trim();
        var requestedExchange = StockExchanges.TryNormalize(stock.Exchange, out var normalizedRequestedExchange)
            ? normalizedRequestedExchange
            : stock.Exchange.Trim();

        var stockLock = StockRefreshLocks.GetOrAdd(stock.Id, static _ => new SemaphoreSlim(1, 1));
        await stockLock.WaitAsync(cancellationToken);
        try
        {
            var persisted = await _dbContext.Stocks.FirstOrDefaultAsync(x => x.Id == stock.Id, cancellationToken);
            if (persisted is null)
            {
                return new StockHistoryRefreshResponse
                {
                    StockId = stock.Id,
                    DeletedPoints = 0,
                    ImportedPoints = 0,
                    RateLimited = false,
                    SkippedNotDue = false,
                    StockNotFound = true,
                };
            }

            if (!StockExchanges.TryNormalize(persisted.Exchange, out var normalizedExchange))
            {
                throw new InvalidOperationException("Stock ticker and exchange must be valid before refreshing history.");
            }

            persisted.Exchange = normalizedExchange;
            EnsureCadenceDefault(persisted);

            if (!string.Equals(persisted.Ticker, requestedTicker, StringComparison.Ordinal)
                || !string.Equals(persisted.Exchange, requestedExchange, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Skipping history refresh for stock {StockId}: listing changed during concurrent operation. Requested={RequestedTicker}/{RequestedExchange} Persisted={PersistedTicker}/{PersistedExchange}",
                    persisted.Id,
                    requestedTicker,
                    requestedExchange,
                    persisted.Ticker,
                    persisted.Exchange);
                return new StockHistoryRefreshResponse
                {
                    StockId = persisted.Id,
                    DeletedPoints = 0,
                    ImportedPoints = 0,
                    RateLimited = false,
                    SkippedNotDue = true,
                    NextDueAtUtc = ComputeNextDueAtUtc(persisted, _timeProvider.GetUtcNow().UtcDateTime),
                };
            }

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var hasHistory = await _dbContext.StockHistoricalPrices
                .AsNoTracking()
                .AnyAsync(x => x.StockId == persisted.Id && x.Interval == "1d", cancellationToken);

            var tier = ResolveTier(persisted, hasHistory, trigger, nowUtc);
            if (tier is null)
            {
                _logger.LogDebug("History refresh skipped as not due for stock {StockId}", persisted.Id);
                return new StockHistoryRefreshResponse
                {
                    StockId = persisted.Id,
                    DeletedPoints = 0,
                    ImportedPoints = 0,
                    RateLimited = false,
                    AppliedTier = null,
                    SkippedNotDue = true,
                    NextDueAtUtc = ComputeNextDueAtUtc(persisted, nowUtc),
                };
            }

            _logger.LogInformation(
                "History refresh due for stock {StockId}: tier={Tier} trigger={Trigger}",
                persisted.Id,
                tier.Value,
                trigger);

            var fetchResult = trigger == StockHistoryRefreshTrigger.Manual
                ? await FetchHistoryBatchesAsync(persisted, cancellationToken)
                : await FetchTierHistoryBatchesAsync(persisted, tier.Value, nowUtc, cancellationToken);
            if (fetchResult.WasRateLimited)
            {
                _logger.LogInformation("Skipping history replacement for stock {StockId} because Yahoo cooldown is active.", stock.Id);
                ScheduleRetry(persisted, tier.Value, nowUtc);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new StockHistoryRefreshResponse
                {
                    StockId = persisted.Id,
                    DeletedPoints = 0,
                    ImportedPoints = 0,
                    RateLimited = true,
                    AppliedTier = tier.Value.ToString(),
                    NextDueAtUtc = ComputeNextDueAtUtc(persisted, nowUtc),
                };
            }

            var batches = BuildPersistenceBatches(persisted, fetchResult.Batches, nowUtc);
            var importedPoints = batches.Sum(batch => batch.Batch.Candles.Count);
            var listingStillCurrent = await _dbContext.Stocks
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == persisted.Id
                         && x.Ticker == requestedTicker
                         && x.Exchange == requestedExchange,
                    cancellationToken);
            if (!listingStillCurrent)
            {
                _logger.LogInformation(
                    "Skipping history persistence for stock {StockId}: listing changed before save. Requested={RequestedTicker}/{RequestedExchange}",
                    persisted.Id,
                    requestedTicker,
                    requestedExchange);
                return new StockHistoryRefreshResponse
                {
                    StockId = persisted.Id,
                    DeletedPoints = 0,
                    ImportedPoints = 0,
                    RateLimited = false,
                    SkippedNotDue = true,
                    NextDueAtUtc = ComputeNextDueAtUtc(persisted, nowUtc),
                };
            }
            if (IsFrankfurtExchange(persisted.Exchange))
            {
                var aggregateBatches = batches
                    .Where(x => string.Equals(x.Interval, "1wk", StringComparison.Ordinal)
                                || string.Equals(x.Interval, "1mo", StringComparison.Ordinal))
                    .ToList();
                var nonAggregateBatches = batches
                    .Where(x => !string.Equals(x.Interval, "1wk", StringComparison.Ordinal)
                                && !string.Equals(x.Interval, "1mo", StringComparison.Ordinal))
                    .ToList();

                await UpsertHistoryBatchesAsync(persisted.Id, nonAggregateBatches, cancellationToken);
                var replaceAllAggregates = trigger == StockHistoryRefreshTrigger.Manual || tier.Value == StockHistoryRefreshTier.FullBackfill;
                await ReplaceFrankfurtAggregateIntervalsAsync(persisted.Id, aggregateBatches, nowUtc, replaceAllAggregates, cancellationToken);
            }
            else
            {
                await UpsertHistoryBatchesAsync(persisted.Id, batches, cancellationToken);
            }
            MarkTierSuccess(persisted, tier.Value, nowUtc);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new StockHistoryRefreshResponse
            {
                StockId = persisted.Id,
                DeletedPoints = 0,
                ImportedPoints = importedPoints,
                RateLimited = false,
                AppliedTier = tier.Value.ToString(),
                NextDueAtUtc = ComputeNextDueAtUtc(persisted, nowUtc),
            };
        }
        catch
        {
            var persisted = await _dbContext.Stocks.FirstOrDefaultAsync(x => x.Id == stock.Id, CancellationToken.None);
            if (persisted is not null)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var hasHistory = await _dbContext.StockHistoricalPrices
                    .AsNoTracking()
                    .AnyAsync(x => x.StockId == persisted.Id && x.Interval == "1d", CancellationToken.None);
                var tier = ResolveTier(persisted, hasHistory, trigger, nowUtc);
                if (tier is not null)
                {
                    ScheduleRetry(persisted, tier.Value, nowUtc);
                    await _dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
            throw;
        }
        finally
        {
            stockLock.Release();
        }
    }

    public Task<StockHistoryRepairDiagnosticsResponse> GetRepairDiagnosticsAsync(Stock stock, CancellationToken cancellationToken = default)
            {
                var configuredProviderSymbol = string.IsNullOrWhiteSpace(stock.ProviderSymbol) ? null : stock.ProviderSymbol.Trim();
                return Task.FromResult(new StockHistoryRepairDiagnosticsResponse
                {
                    StockId = stock.Id,
                    Ticker = stock.Ticker,
                    Exchange = stock.Exchange,
                    Name = stock.Name,
                    ConfiguredProviderSymbol = configuredProviderSymbol,
                    EffectiveProviderSymbol = ResolveStockProviderSymbol(stock),
                    ResultBucket = "success",
                    ResetPerformed = false,
                    FinalRows = 0,
                    DeletedRows = 0,
                    InsertedRows = 0,
                });
            }

            public async Task<FrankfurtAggregateRebuildResponse> RebuildFrankfurtAggregatesAsync(
                int batchSize = 25,
                int? afterStockId = null,
                CancellationToken cancellationToken = default)
            {
                var normalizedBatchSize = Math.Clamp(batchSize, 1, 200);
                var cursor = afterStockId.GetValueOrDefault();
                var errors = new List<string>();

                var stocks = await _dbContext.Stocks
                    .AsNoTracking()
                    .Where(s => s.Id > cursor && s.Exchange == StockExchanges.Frankfurt && !string.IsNullOrWhiteSpace(s.Ticker))
                    .OrderBy(s => s.Id)
                    .Take(normalizedBatchSize)
                    .ToListAsync(cancellationToken);

                var rebuilt = 0;
                var failed = 0;
                var stoppedDueToRateLimit = false;
                foreach (var stock in stocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var refresh = await RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Manual, cancellationToken);
                        if (refresh.RateLimited)
                        {
                            stoppedDueToRateLimit = true;
                            break;
                        }

                        rebuilt++;
                        cursor = stock.Id;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        cursor = stock.Id;
                        errors.Add($"StockId={stock.Id}, Ticker={stock.Ticker}: {ex.GetType().Name}");
                        _logger.LogWarning(ex, "Failed Frankfurt aggregate rebuild for stock {StockId}", stock.Id);
                    }
                }

                var hasMore = await _dbContext.Stocks
                    .AsNoTracking()
                    .AnyAsync(
                        s => s.Id > cursor
                             && s.Exchange == StockExchanges.Frankfurt
                             && !string.IsNullOrWhiteSpace(s.Ticker),
                        cancellationToken);

                return new FrankfurtAggregateRebuildResponse
                {
                    BatchSize = normalizedBatchSize,
                    StartedAfterStockId = afterStockId,
                    NextAfterStockId = cursor,
                    ProcessedStocks = rebuilt + failed,
                    RebuiltStocks = rebuilt,
                    FailedStocks = failed,
                    StoppedDueToRateLimit = stoppedDueToRateLimit,
                    HasMore = hasMore || stoppedDueToRateLimit,
                    Errors = errors,
                };
            }

            public async Task<StockHistoryRepairDiagnosticsResponse> ValidateProviderSymbolAsync(
                Stock stock,
                string? candidateProviderSymbol,
                CancellationToken cancellationToken = default)
            {
                var persisted = await _dbContext.Stocks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == stock.Id, cancellationToken);
                if (persisted is null)
                {
                    return new StockHistoryRepairDiagnosticsResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Name = stock.Name,
                        ConfiguredProviderSymbol = stock.ProviderSymbol,
                        EffectiveProviderSymbol = ResolveStockProviderSymbol(stock),
                        CandidateOverrideSymbol = NormalizeCandidateProviderSymbol(candidateProviderSymbol),
                        ResultBucket = "failed",
                        Errors = new[] { "Акция не найдена." },
                    };
                }

                var preparation = await PrepareHardResetAsync(persisted, candidateProviderSymbol, cancellationToken);
                return preparation.Diagnostics;
            }

            public async Task<StockHistoryRepairDiagnosticsResponse> HardResetHistoryAsync(
                Stock stock,
                string? candidateProviderSymbol,
                CancellationToken cancellationToken = default)
            {
                var stockLock = StockRefreshLocks.GetOrAdd(stock.Id, static _ => new SemaphoreSlim(1, 1));
                await stockLock.WaitAsync(cancellationToken);
                try
                {
                    var persisted = await _dbContext.Stocks.FirstOrDefaultAsync(x => x.Id == stock.Id, cancellationToken);
                    if (persisted is null)
                    {
                        return new StockHistoryRepairDiagnosticsResponse
                        {
                            StockId = stock.Id,
                            Ticker = stock.Ticker,
                            Exchange = stock.Exchange,
                            Name = stock.Name,
                            ConfiguredProviderSymbol = stock.ProviderSymbol,
                            EffectiveProviderSymbol = ResolveStockProviderSymbol(stock),
                            CandidateOverrideSymbol = NormalizeCandidateProviderSymbol(candidateProviderSymbol),
                            ResultBucket = "failed",
                            Errors = new[] { "Акция не найдена." },
                        };
                    }

                    var preparation = await PrepareHardResetAsync(persisted, candidateProviderSymbol, cancellationToken);
                    if (preparation.Diagnostics.ResultBucket is not ("success" or "partial"))
                    {
                        return preparation.Diagnostics with { ResetPerformed = false };
                    }

                    var overrideProviderSymbol = preparation.CandidateProviderSymbol;
                    var replacementRows = preparation.ReplacementRows;

                    try
                    {
                        var (deletedRows, insertedRows) = await ReplaceHistoryAndOptionallyOverrideProviderSymbolAsync(
                            persisted,
                            overrideProviderSymbol,
                            replacementRows,
                            cancellationToken);

                        var finalRows = await _dbContext.StockHistoricalPrices
                            .AsNoTracking()
                            .CountAsync(x => x.StockId == persisted.Id, cancellationToken);

                        var intervalFinalCounts = await _dbContext.StockHistoricalPrices
                            .AsNoTracking()
                            .Where(x => x.StockId == persisted.Id)
                            .GroupBy(x => x.Interval)
                            .Select(group => new { Interval = group.Key, Count = group.Count() })
                            .ToDictionaryAsync(x => x.Interval, x => x.Count, cancellationToken);

                        var updatedIntervals = preparation.Diagnostics.Intervals
                            .Select(interval => interval with
                            {
                                InsertedCount = interval.AcceptedCount,
                                FinalCount = intervalFinalCounts.TryGetValue(interval.Interval, out var count) ? count : 0,
                            })
                            .ToList();

                        return preparation.Diagnostics with
                        {
                            ResultBucket = preparation.Diagnostics.ResultBucket,
                            DeletedRows = deletedRows,
                            InsertedRows = insertedRows,
                            FinalRows = finalRows,
                            ResetPerformed = true,
                            Intervals = updatedIntervals,
                            EffectiveProviderSymbol = ResolveStockProviderSymbol(persisted),
                            ConfiguredProviderSymbol = persisted.ProviderSymbol,
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        return preparation.Diagnostics with
                        {
                            ResultBucket = "failed",
                            ResetPerformed = false,
                            Errors = preparation.Diagnostics.Errors.Append("Сбой записи в БД: полное восстановление не выполнено.").ToArray(),
                        };
                    }
                }
                finally
                {
                    stockLock.Release();
                }
            }

            private async Task<HardResetPreparation> PrepareHardResetAsync(
                Stock stock,
                string? candidateProviderSymbol,
                CancellationToken cancellationToken)
            {
                var warnings = new List<string>();
                var errors = new List<string>();
                var normalizedCandidate = NormalizeCandidateProviderSymbol(candidateProviderSymbol);
                var configuredProviderSymbol = string.IsNullOrWhiteSpace(stock.ProviderSymbol) ? null : stock.ProviderSymbol.Trim();
                var effectiveProviderSymbol = normalizedCandidate ?? ResolveStockProviderSymbol(stock);

                if (string.IsNullOrWhiteSpace(effectiveProviderSymbol))
                {
                    errors.Add("Не удалось определить effective ProviderSymbol.");
                }
                else if (!ProviderSymbolRegex.IsMatch(effectiveProviderSymbol))
                {
                    errors.Add("Кандидат ProviderSymbol содержит недопустимые символы.");
                }

                var baseDiagnostics = new StockHistoryRepairDiagnosticsResponse
                {
                    StockId = stock.Id,
                    Ticker = stock.Ticker,
                    Exchange = stock.Exchange,
                    Name = stock.Name,
                    ConfiguredProviderSymbol = configuredProviderSymbol,
                    EffectiveProviderSymbol = effectiveProviderSymbol,
                    CandidateOverrideSymbol = normalizedCandidate,
                    Provider = "yahoo",
                    ResultBucket = "validationFailed",
                    Warnings = warnings,
                    Errors = errors,
                };

                if (errors.Count > 0)
                {
                    return new HardResetPreparation(baseDiagnostics, normalizedCandidate, Array.Empty<StockHistoricalPrice>());
                }

                var monthly = await FetchCandlesAsync(effectiveProviderSymbol, "1mo", "5y", cancellationToken);
                var weekly = await FetchCandlesAsync(effectiveProviderSymbol, "1wk", "1y", cancellationToken);
                var dailyRange = IsFrankfurtExchange(stock.Exchange) ? "5y" : "2y";
                var daily = await FetchCandlesAsync(effectiveProviderSymbol, "1d", dailyRange, cancellationToken);
                var hourly = await FetchCandlesAsync(effectiveProviderSymbol, "1h", "7d", cancellationToken);
                var fiveMinute = await FetchCandlesAsync(effectiveProviderSymbol, "5m", "1d", cancellationToken);
                var tenMinute = fiveMinute.WasRateLimited ? CandleFetchResult.RateLimited() : CandleFetchResult.Success(AggregateToTenMinute(fiveMinute.Batch));

                if (monthly.WasRateLimited || weekly.WasRateLimited || daily.WasRateLimited || hourly.WasRateLimited || fiveMinute.WasRateLimited)
                {
                    var rateLimitedDiagnostics = baseDiagnostics with
                    {
                        ResultBucket = "rateLimited",
                        Errors = new[] { "Поставщик временно ограничил запросы. Сброс не выполнен." },
                    };
                    return new HardResetPreparation(rateLimitedDiagnostics, normalizedCandidate, Array.Empty<StockHistoricalPrice>());
                }

                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var weeklyBatch = IsFrankfurtExchange(stock.Exchange)
                    ? AggregateFrankfurtDailyBatch(daily.Batch, "1wk", nowUtc)
                    : weekly.Batch;
                var monthlyBatch = IsFrankfurtExchange(stock.Exchange)
                    ? AggregateFrankfurtDailyBatch(daily.Batch, "1mo", nowUtc)
                    : monthly.Batch;

                var intervalPlan = new (string Interval, CandleBatch Batch, bool Required, int MinCount)[]
                {
                    ("1mo", monthlyBatch, true, 12),
                    ("1wk", weeklyBatch, true, 26),
                    ("1d", daily.Batch, true, 120),
                    ("1h", hourly.Batch, false, 1),
                    ("10m", tenMinute.Batch, false, 1),
                };

                var replacementRows = new List<StockHistoricalPrice>();
                var intervalDiagnostics = new List<StockHistoryRepairIntervalDiagnosticsResponse>();

                foreach (var (interval, batch, required, minCount) in intervalPlan)
                {
                    var requestedCount = batch.Candles.Count;
                    var accepted = new List<CandleData>();
                    var rejectedCount = 0;
                    var seenTimestamps = new HashSet<DateTime>();
                    DateTime? previousTimestamp = null;

                    foreach (var candle in batch.Candles.OrderBy(x => x.Timestamp))
                    {
                        var isOrdered = !previousTimestamp.HasValue || candle.Timestamp > previousTimestamp.Value;
                        var isUnique = seenTimestamps.Add(candle.Timestamp);
                        var timestampValid = candle.Timestamp <= nowUtc;
                        var ohlcValid = IsValidOhlc(candle);

                        if (!isOrdered || !isUnique || !timestampValid || !ohlcValid)
                        {
                            rejectedCount++;
                            continue;
                        }

                        accepted.Add(candle);
                        previousTimestamp = candle.Timestamp;
                    }

                    if (required && accepted.Count < minCount)
                    {
                        errors.Add($"Недостаточно валидных данных для интервала {interval}: {accepted.Count} < {minCount}.");
                    }

                    if (!required && accepted.Count == 0)
                    {
                        warnings.Add($"Интервал {interval} недоступен у поставщика и будет пропущен.");
                    }

                    foreach (var candle in accepted)
                    {
                        replacementRows.Add(new StockHistoricalPrice
                        {
                            StockId = stock.Id,
                            Timestamp = candle.Timestamp,
                            Interval = interval,
                            Open = candle.Open,
                            High = candle.High,
                            Low = candle.Low,
                            Close = candle.Close,
                            AdjustedClose = candle.AdjustedClose,
                            QuoteCurrency = batch.QuoteCurrency,
                            FinancialCurrency = batch.FinancialCurrency,
                            NormalizedQuoteCurrency = batch.NormalizedQuoteCurrency,
                            QuoteUnitMultiplier = batch.QuoteUnitMultiplier,
                            Volume = candle.Volume,
                            IsQuoteDerived = false,
                        });
                    }

                    intervalDiagnostics.Add(new StockHistoryRepairIntervalDiagnosticsResponse
                    {
                        Interval = interval,
                        RequestedCount = requestedCount,
                        FetchedCount = requestedCount,
                        AcceptedCount = accepted.Count,
                        RejectedCount = rejectedCount,
                        InsertedCount = 0,
                        FinalCount = 0,
                        FirstTimestampUtc = accepted.Count > 0 ? accepted.First().Timestamp : null,
                        LastTimestampUtc = accepted.Count > 0 ? accepted.Last().Timestamp : null,
                        IsUnavailable = !required && accepted.Count == 0,
                        Message = !required && accepted.Count == 0 ? "Поставщик не вернул исторические данные для интервала." : null,
                    });
                }

                var hasAnyAccepted = intervalDiagnostics.Any(x => x.AcceptedCount > 0);
                var resultBucket = errors.Count > 0
                    ? "validationFailed"
                    : !hasAnyAccepted
                        ? "empty"
                        : warnings.Count > 0
                            ? "partial"
                            : "success";

                var latestAcceptedPoint = replacementRows.Count > 0
                    ? replacementRows.Max(x => x.Timestamp)
                    : (DateTime?)null;
                var providerCurrency = intervalPlan
                    .Select(plan => plan.Batch.NormalizedQuoteCurrency ?? plan.Batch.QuoteCurrency)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

                var diagnostics = baseDiagnostics with
                {
                    ResultBucket = resultBucket,
                    ProviderQuoteSymbol = effectiveProviderSymbol,
                    ProviderCurrency = providerCurrency,
                    ProviderPriceTimestampUtc = latestAcceptedPoint,
                    Intervals = intervalDiagnostics,
                    Warnings = warnings,
                    Errors = errors,
                };

                return new HardResetPreparation(diagnostics, normalizedCandidate, replacementRows);
            }

            private static string? NormalizeCandidateProviderSymbol(string? value)
            {
                var normalized = value?.Trim();
                return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
            }

            private static bool IsValidOhlc(CandleData candle)
            {
                if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
                {
                    return false;
                }

                if (candle.Low > candle.High)
                {
                    return false;
                }

                var maxBody = Math.Max(candle.Open, candle.Close);
                var minBody = Math.Min(candle.Open, candle.Close);
                return candle.High >= maxBody && candle.Low <= minBody;
            }

            private async Task<(int DeletedRows, int InsertedRows)> ReplaceHistoryAndOptionallyOverrideProviderSymbolAsync(
                Stock persistedStock,
                string? overrideProviderSymbol,
                IReadOnlyCollection<StockHistoricalPrice> replacementRows,
                CancellationToken cancellationToken)
            {
                if (!_dbContext.Database.IsRelational())
                {
                    var existingRows = await _dbContext.StockHistoricalPrices
                        .Where(x => x.StockId == persistedStock.Id)
                        .ToListAsync(cancellationToken);

                    var deletedRows = existingRows.Count;
                    if (deletedRows > 0)
                    {
                        _dbContext.StockHistoricalPrices.RemoveRange(existingRows);
                    }

                    if (!string.IsNullOrWhiteSpace(overrideProviderSymbol))
                    {
                        persistedStock.ProviderSymbol = overrideProviderSymbol;
                    }

                    _dbContext.StockHistoricalPrices.AddRange(CloneReplacementRows(replacementRows));
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return (deletedRows, replacementRows.Count);
                }

                var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
                var deleted = 0;

                await executionStrategy.ExecuteAsync(async () =>
                {
                    _dbContext.ChangeTracker.Clear();
                    var trackedStock = await _dbContext.Stocks.FirstAsync(x => x.Id == persistedStock.Id, cancellationToken);
                    await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                    var existingRows = await _dbContext.StockHistoricalPrices
                        .Where(x => x.StockId == persistedStock.Id)
                        .ToListAsync(cancellationToken);
                    deleted = existingRows.Count;
                    if (deleted > 0)
                    {
                        _dbContext.StockHistoricalPrices.RemoveRange(existingRows);
                    }

                    if (!string.IsNullOrWhiteSpace(overrideProviderSymbol))
                    {
                        trackedStock.ProviderSymbol = overrideProviderSymbol;
                    }

                    _dbContext.StockHistoricalPrices.AddRange(CloneReplacementRows(replacementRows));
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                });

                _dbContext.ChangeTracker.Clear();
                return (deleted, replacementRows.Count);
            }

    public async Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
    {
        var normalizedRange = NormalizeRange(range);
        var interval = GetInterval(normalizedRange);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var intradayWindow = BuildIntradayWindow(stock, normalizedRange, nowUtc);
        var from = intradayWindow?.QueryFromUtc ?? GetFromTimestamp(normalizedRange, nowUtc);

        var (data, usedDailyAggregationFallback) = await LoadHistoryWithDailyAggregationFallbackAsync(
            stock,
            interval,
            from,
            normalizedRange,
            intradayWindow,
            nowUtc,
            cancellationToken);
        var shouldRefreshStaleIntraday = ShouldRefreshIntradayOnDemand(stock, normalizedRange, interval, data, nowUtc);
        var shouldRefresh = data.Count == 0 || data.Any(NeedsMetadataBackfill) || shouldRefreshStaleIntraday;
        var onDemandRefreshFailed = false;
        if (shouldRefresh && !string.IsNullOrWhiteSpace(stock.Ticker))
        {
            try
            {
                var refresh = await RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic, cancellationToken);
                onDemandRefreshFailed = refresh.RateLimited;
                (data, usedDailyAggregationFallback) = await LoadHistoryWithDailyAggregationFallbackAsync(
                    stock,
                    interval,
                    from,
                    normalizedRange,
                    intradayWindow,
                    nowUtc,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                onDemandRefreshFailed = true;
                _logger.LogWarning(ex, "On-demand stock history sync failed for stock {StockId}", stock.Id);
            }
        }

        var currencyMetadata = data.LastOrDefault();
        var conversionContext = await _stockQuoteConversionService.GetConversionContextAsync(
            currencyMetadata?.QuoteCurrency,
            currencyMetadata?.FinancialCurrency,
            cancellationToken);
        var volumeMetrics = BuildVolumeMetrics(
            data,
            interval,
            conversionContext,
            usedDailyAggregationFallback || (IsFrankfurtExchange(stock.Exchange) && IsLongRangeAggregateInterval(interval)));
        var asOfUtc = data.LastOrDefault()?.Timestamp;
        var isPotentiallyStale = IsPotentiallyStale(interval, asOfUtc, nowUtc);
        var staleReason = BuildHistoryStaleReason(stock.Exchange, normalizedRange, interval, asOfUtc, nowUtc, isPotentiallyStale, onDemandRefreshFailed);
        var unavailableReason = data.Count == 0
            ? BuildUnavailableReason(stock, normalizedRange, onDemandRefreshFailed)
            : null;

        return new StockHistoryResponse
        {
            Range = normalizedRange,
            Interval = interval,
            Currency = conversionContext.Metadata.QuoteCurrency,
            FinancialCurrency = conversionContext.Metadata.FinancialCurrency,
            NormalizedQuoteCurrency = conversionContext.Metadata.NormalizedQuoteCurrency,
            QuoteUnitMultiplier = conversionContext.Metadata.QuoteUnitMultiplier,
            RateToEur = conversionContext.ExchangeRate.RateToEur,
            RateTimestampUtc = conversionContext.ExchangeRate.RateTimestampUtc,
            RateSource = conversionContext.ExchangeRate.Source,
            ConversionWarning = conversionContext.Warning,
            AsOfUtc = asOfUtc,
            WindowStartUtc = intradayWindow?.WindowStartUtc,
            WindowEndUtc = intradayWindow?.WindowEndUtc,
            PreviousSessionStartUtc = intradayWindow?.PreviousSessionStartUtc,
            PreviousSessionEndUtc = intradayWindow?.PreviousSessionEndUtc,
            CurrentSessionStartUtc = intradayWindow?.CurrentSessionStartUtc,
            CurrentSessionEndUtc = intradayWindow?.CurrentSessionEndUtc,
            CurrentSessionHasCandles = intradayWindow?.CurrentSessionHasCandles(data),
            IsPotentiallyStale = isPotentiallyStale,
            StaleReason = staleReason,
            UnavailableReason = unavailableReason,
            VolumeMetrics = volumeMetrics,
            Points = data
                .Select(point => _stockQuoteConversionService.BuildHistoryPointResponse(point, conversionContext))
                .ToList()
        };
    }

    private async Task<HistoryBatchFetchResult> FetchHistoryBatchesAsync(Stock stock, CancellationToken cancellationToken)
    {
        var providerSymbol = ResolveStockProviderSymbol(stock);

        var monthly = await FetchCandlesAsync(providerSymbol, "1mo", "5y", cancellationToken);
        if (monthly.WasRateLimited) return HistoryBatchFetchResult.RateLimited();
        var weekly = await FetchCandlesAsync(providerSymbol, "1wk", "1y", cancellationToken);
        if (weekly.WasRateLimited) return HistoryBatchFetchResult.RateLimited();
        // Daily history uses a 2-year lookback (≈504 trading days) to support SMA200 (252 obs),
        // annualized volatility (60-day window), and 12-month return calculations.
        var dailyRange = IsFrankfurtExchange(stock.Exchange) ? "5y" : "2y";
        var daily = await FetchCandlesAsync(providerSymbol, "1d", dailyRange, cancellationToken);
        if (daily.WasRateLimited) return HistoryBatchFetchResult.RateLimited();
        var hourly = await FetchCandlesAsync(providerSymbol, "1h", "7d", cancellationToken);
        if (hourly.WasRateLimited) return HistoryBatchFetchResult.RateLimited();
        var fiveMinute = await FetchCandlesAsync(providerSymbol, "5m", "1d", cancellationToken);
        if (fiveMinute.WasRateLimited) return HistoryBatchFetchResult.RateLimited();
        var tenMinute = AggregateToTenMinute(fiveMinute);

        return HistoryBatchFetchResult.Success(
        [
            new IntervalBatch("1mo", monthly),
            new IntervalBatch("1wk", weekly),
            new IntervalBatch("1d", daily),
            new IntervalBatch("1h", hourly),
            new IntervalBatch("10m", tenMinute),
        ]);
    }

    private async Task<HistoryBatchFetchResult> FetchTierHistoryBatchesAsync(
        Stock stock,
        StockHistoryRefreshTier tier,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var providerSymbol = ResolveStockProviderSymbol(stock);
        var lookbackDays = tier switch
        {
            StockHistoryRefreshTier.Incremental => _options.IncrementalLookbackDays,
            StockHistoryRefreshTier.Reconciliation => _options.ReconciliationLookbackDays,
            _ => _options.FullBackfillLookbackDays,
        };
        if (tier == StockHistoryRefreshTier.FullBackfill)
        {
            lookbackDays = Math.Max(lookbackDays, FullBackfillMinimumLookbackDays);
        }

        var fromUtc = nowUtc.Date.AddDays(-lookbackDays);
        var daily = await FetchCandlesByDateRangeAsync(providerSymbol, "1d", fromUtc, nowUtc, cancellationToken);
        if (daily.WasRateLimited)
        {
            return HistoryBatchFetchResult.RateLimited();
        }

        var hourly = await FetchCandlesAsync(providerSymbol, "1h", "7d", cancellationToken);
        if (hourly.WasRateLimited)
        {
            return HistoryBatchFetchResult.RateLimited();
        }

        var fiveMinute = await FetchCandlesAsync(providerSymbol, "5m", "1d", cancellationToken);
        if (fiveMinute.WasRateLimited)
        {
            return HistoryBatchFetchResult.RateLimited();
        }

        var tenMinute = AggregateToTenMinute(fiveMinute);
        var batches = new List<IntervalBatch>
        {
            new("1d", daily),
            new("1h", hourly),
            new("10m", tenMinute),
        };
        if (tier is StockHistoryRefreshTier.Reconciliation or StockHistoryRefreshTier.FullBackfill)
        {
            var weekly = AggregateDailyBatch(daily, "1wk", nowUtc, stock.Exchange);
            var monthly = AggregateDailyBatch(daily, "1mo", nowUtc, stock.Exchange);
            batches.Add(new IntervalBatch("1wk", weekly));
            batches.Add(new IntervalBatch("1mo", monthly));
        }

        return HistoryBatchFetchResult.Success(batches);
    }

    private async Task UpsertHistoryBatchesAsync(int stockId, IEnumerable<IntervalBatch> batches, CancellationToken cancellationToken)
    {
        foreach (var entry in batches)
        {
            await UpsertCandlesAsync(stockId, entry.Interval, entry.Batch, cancellationToken);
        }
    }

    private async Task ReplaceFrankfurtAggregateIntervalsAsync(
        int stockId,
        IReadOnlyCollection<IntervalBatch> aggregateBatches,
        DateTime nowUtc,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.StockHistoricalPrices
            .Where(x => x.StockId == stockId && (x.Interval == "1wk" || x.Interval == "1mo"))
            .ToListAsync(cancellationToken);

        List<StockHistoricalPrice> toRemove;
        if (replaceAll)
        {
            toRemove = existing;
        }
        else if (TradingSessionCalendar.TryGetSessionSpec(StockExchanges.Frankfurt, out var sessionSpec))
        {
            var berlinTimeZone = TradingSessionCalendar.TryResolveTimeZone(sessionSpec);
            if (berlinTimeZone is not null)
            {
                var localNowDate = TradingSessionCalendar.ConvertUtcToLocalDate(nowUtc, berlinTimeZone);
                var currentWeekStart = GetIsoWeekStart(localNowDate);
                var currentMonthStart = new DateOnly(localNowDate.Year, localNowDate.Month, 1);

                var refreshedWeekStarts = aggregateBatches
                    .Where(x => x.Interval == "1wk")
                    .SelectMany(x => x.Batch.Candles)
                    .Select(c => GetIsoWeekStart(TradingSessionCalendar.ConvertUtcToLocalDate(c.Timestamp, berlinTimeZone)))
                    .ToHashSet();
                var refreshedMonthStarts = aggregateBatches
                    .Where(x => x.Interval == "1mo")
                    .SelectMany(x => x.Batch.Candles)
                    .Select(c =>
                    {
                        var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(c.Timestamp, berlinTimeZone);
                        return new DateOnly(localDate.Year, localDate.Month, 1);
                    })
                    .ToHashSet();

                toRemove = existing.Where(row =>
                {
                    var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(row.Timestamp, berlinTimeZone);
                    if (row.Interval == "1wk")
                    {
                        var weekStart = GetIsoWeekStart(localDate);
                        return weekStart >= currentWeekStart || refreshedWeekStarts.Contains(weekStart);
                    }

                    var monthStart = new DateOnly(localDate.Year, localDate.Month, 1);
                    return monthStart >= currentMonthStart || refreshedMonthStarts.Contains(monthStart);
                }).ToList();
            }
            else
            {
                toRemove = new List<StockHistoricalPrice>();
            }
        }
        else
        {
            toRemove = new List<StockHistoricalPrice>();
        }

        if (toRemove.Count > 0)
        {
            _dbContext.StockHistoricalPrices.RemoveRange(toRemove);
        }

        foreach (var batch in aggregateBatches)
        {
            if (!replaceAll)
            {
                await UpsertCandlesAsync(stockId, batch.Interval, batch.Batch, cancellationToken);
                continue;
            }

            foreach (var candle in batch.Batch.Candles)
            {
                _dbContext.StockHistoricalPrices.Add(new StockHistoricalPrice
                {
                    StockId = stockId,
                    Timestamp = candle.Timestamp,
                    Interval = batch.Interval,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    AdjustedClose = candle.AdjustedClose,
                    QuoteCurrency = batch.Batch.QuoteCurrency,
                    FinancialCurrency = batch.Batch.FinancialCurrency,
                    NormalizedQuoteCurrency = batch.Batch.NormalizedQuoteCurrency,
                    QuoteUnitMultiplier = batch.Batch.QuoteUnitMultiplier,
                    Volume = candle.Volume,
                    IsQuoteDerived = false,
                });
            }
        }

        if (replaceAll || toRemove.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private List<IntervalBatch> BuildPersistenceBatches(Stock stock, IReadOnlyList<IntervalBatch> fetchedBatches, DateTime nowUtc)
    {
        var batches = fetchedBatches.ToList();
        if (!IsFrankfurtExchange(stock.Exchange))
        {
            return batches;
        }

        var dailyBatchEntry = batches.FirstOrDefault(x => string.Equals(x.Interval, "1d", StringComparison.Ordinal));
        if (dailyBatchEntry is null || dailyBatchEntry.Batch.Candles.Count == 0)
        {
            return batches;
        }
        var dailyBatch = dailyBatchEntry.Batch;

        batches.RemoveAll(x => string.Equals(x.Interval, "1wk", StringComparison.Ordinal) || string.Equals(x.Interval, "1mo", StringComparison.Ordinal));
        batches.Add(new IntervalBatch("1wk", AggregateFrankfurtDailyBatch(dailyBatch, "1wk", nowUtc)));
        batches.Add(new IntervalBatch("1mo", AggregateFrankfurtDailyBatch(dailyBatch, "1mo", nowUtc)));
        return batches;
    }

    private sealed record IntervalBatch(string Interval, CandleBatch Batch);
    private sealed record HardResetPreparation(
        StockHistoryRepairDiagnosticsResponse Diagnostics,
        string? CandidateProviderSymbol,
        IReadOnlyCollection<StockHistoricalPrice> ReplacementRows);

    private static List<StockHistoricalPrice> BuildReplacementRows(int stockId, IEnumerable<IntervalBatch> batches)
    {
        var replacementRows = new List<StockHistoricalPrice>();
        foreach (var entry in batches)
        {
            foreach (var candle in entry.Batch.Candles)
            {
                replacementRows.Add(new StockHistoricalPrice
                {
                    StockId = stockId,
                    Timestamp = candle.Timestamp,
                    Interval = entry.Interval,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    AdjustedClose = candle.AdjustedClose,
                    QuoteCurrency = entry.Batch.QuoteCurrency,
                    FinancialCurrency = entry.Batch.FinancialCurrency,
                    NormalizedQuoteCurrency = entry.Batch.NormalizedQuoteCurrency,
                    QuoteUnitMultiplier = entry.Batch.QuoteUnitMultiplier,
                    Volume = candle.Volume,
                    IsQuoteDerived = false,
                });
            }
        }

        return replacementRows;
    }

    private async Task<int> ReplaceHistoryAsync(
        int stockId,
        IReadOnlyCollection<StockHistoricalPrice> replacementRows,
        CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            return await ReplaceHistoryWithoutTransactionAsync(stockId, replacementRows, cancellationToken);
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
        var deletedPoints = 0;

        await executionStrategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var existingRows = await _dbContext.StockHistoricalPrices
                .Where(x => x.StockId == stockId)
                .ToListAsync(cancellationToken);
            deletedPoints = existingRows.Count;

            if (deletedPoints > 0)
            {
                _dbContext.StockHistoricalPrices.RemoveRange(existingRows);
            }

            _dbContext.StockHistoricalPrices.AddRange(CloneReplacementRows(replacementRows));

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        _dbContext.ChangeTracker.Clear();
        return deletedPoints;
    }

    private async Task<int> ReplaceHistoryWithoutTransactionAsync(
        int stockId,
        IReadOnlyCollection<StockHistoricalPrice> replacementRows,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();

        var existingRows = await _dbContext.StockHistoricalPrices
            .Where(x => x.StockId == stockId)
            .ToListAsync(cancellationToken);
        var deletedPoints = existingRows.Count;

        if (deletedPoints > 0)
        {
            _dbContext.StockHistoricalPrices.RemoveRange(existingRows);
        }

        _dbContext.StockHistoricalPrices.AddRange(CloneReplacementRows(replacementRows));
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();

        return deletedPoints;
    }

    private static IEnumerable<StockHistoricalPrice> CloneReplacementRows(IEnumerable<StockHistoricalPrice> replacementRows)
    {
        foreach (var row in replacementRows)
        {
            yield return new StockHistoricalPrice
            {
                StockId = row.StockId,
                Timestamp = row.Timestamp,
                Interval = row.Interval,
                Open = row.Open,
                High = row.High,
                Low = row.Low,
                Close = row.Close,
                AdjustedClose = row.AdjustedClose,
                QuoteCurrency = row.QuoteCurrency,
                FinancialCurrency = row.FinancialCurrency,
                NormalizedQuoteCurrency = row.NormalizedQuoteCurrency,
                QuoteUnitMultiplier = row.QuoteUnitMultiplier,
                Volume = row.Volume,
                IsQuoteDerived = row.IsQuoteDerived,
            };
        }
    }

    private static string NormalizeRange(string range)
    {
        var value = (range ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "5y" => "5y",
            "3y" => "3y",
            "1y" => "1y",
            "6m" => "6m",
            "3m" => "3m",
            "1m" => "1m",
            "1w" => "1w",
            "24h" => "24h",
            "today" => "today",
            _ => "5y"
        };
    }

    private static DateTime GetFromTimestamp(string normalizedRange, DateTime now)
    {
        return normalizedRange switch
        {
            "5y" => now.AddYears(-5),
            "3y" => now.AddYears(-3),
            "1y" => now.AddYears(-1),
            "6m" => now.AddMonths(-6),
            "3m" => now.AddMonths(-3),
            "1m" => now.AddMonths(-1),
            "1w" => now.AddDays(-7),
            "24h" => now.AddHours(-24),
            "today" => now.Date,
            _ => now.AddYears(-5)
        };
    }

    private static List<StockHistoricalPrice> FilterRowsForRange(
        IReadOnlyList<StockHistoricalPrice> data,
        IntradaySessionRange? intradayWindow,
        string normalizedRange)
    {
        if (intradayWindow is null || data.Count == 0 || normalizedRange is not ("24h" or "today"))
        {
            return data.ToList();
        }

        var filtered = normalizedRange == "today"
            ? data.Where(x => intradayWindow.IsInCurrentSession(x.Timestamp)).ToList()
            : data.Where(x => intradayWindow.IsInPreviousOrCurrentSession(x.Timestamp)).ToList();

        if (normalizedRange == "24h" && filtered.All(x => !intradayWindow.IsInPreviousSession(x.Timestamp)))
        {
            var fallbackPreviousDate = intradayWindow.TryFindLatestPreviousSessionDateWithData(data);
            if (fallbackPreviousDate.HasValue)
            {
                filtered = data.Where(x => intradayWindow.IsInPreviousOrCurrentSession(x.Timestamp, fallbackPreviousDate.Value)).ToList();
            }
        }

        return filtered;
    }

    private static IntradaySessionRange? BuildIntradayWindow(Stock stock, string normalizedRange, DateTime nowUtc)
    {
        if (normalizedRange is not ("24h" or "today"))
        {
            return null;
        }

        if (!TradingSessionCalendar.TryGetSessionSpec(stock.Exchange, out var sessionSpec))
        {
            return null;
        }

        var timeZone = TradingSessionCalendar.TryResolveTimeZone(sessionSpec);
        if (timeZone is null)
        {
            return null;
        }

        var localNowDate = TradingSessionCalendar.ConvertUtcToLocalDate(nowUtc, timeZone);
        var currentSessionDate = TradingSessionCalendar.GetNextTradingDay(localNowDate, sessionSpec);
        var previousSessionDate = TradingSessionCalendar.GetPreviousTradingDay(currentSessionDate, sessionSpec);

        var previousSession = TradingSessionCalendar.BuildSessionWindow(previousSessionDate, sessionSpec, timeZone);
        var currentSession = TradingSessionCalendar.BuildSessionWindow(currentSessionDate, sessionSpec, timeZone);
        var queryFromUtc = normalizedRange == "today"
            ? currentSession.SessionStartUtc
            : nowUtc.AddDays(-10);

        return new IntradaySessionRange(
            sessionSpec,
            timeZone,
            previousSession,
            currentSession,
            queryFromUtc);
    }

    private static string GetInterval(string normalizedRange) => normalizedRange switch
    {
        "5y" or "3y" => "1mo",
        "1y" => "1wk",
        "6m" or "3m" or "1m" => "1d",
        "1w" => "1h",
        "24h" or "today" => "10m",
        _ => "1mo"
    };

    private static bool IsFrankfurtExchange(string? exchange)
        => StockExchanges.TryNormalize(exchange, out var normalizedExchange)
            && string.Equals(normalizedExchange, StockExchanges.Frankfurt, StringComparison.Ordinal);

    private static bool IsLongRangeAggregateInterval(string interval)
        => string.Equals(interval, "1wk", StringComparison.Ordinal)
           || string.Equals(interval, "1mo", StringComparison.Ordinal);

    private async Task<(List<StockHistoricalPrice> Data, bool UsedDailyAggregationFallback)> LoadHistoryWithDailyAggregationFallbackAsync(
        Stock stock,
        string interval,
        DateTime from,
        string normalizedRange,
        IntradaySessionRange? intradayWindow,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var aggregateRows = await LoadHistoryRowsAsync(stock.Id, interval, from, cancellationToken);
        aggregateRows = FilterRowsForRange(aggregateRows, intradayWindow, normalizedRange);

        if (!IsLongRangeAggregateInterval(interval))
        {
            return (aggregateRows, false);
        }

        var dailyRows = await LoadHistoryRowsAsync(stock.Id, "1d", from, cancellationToken);
        if (dailyRows.Count == 0)
        {
            return (aggregateRows, false);
        }

        var fallbackRows = AggregateDailyFromRows(stock.Id, dailyRows, interval, nowUtc, stock.Exchange)
            .Where(x => x.Timestamp >= from)
            .OrderBy(x => x.Timestamp)
            .ToList();

        if (fallbackRows.Count == 0)
        {
            return (aggregateRows, false);
        }

        if (aggregateRows.Count == 0)
        {
            return (fallbackRows, true);
        }

        var aggregateCoverage = ComputeCoverageRatio(aggregateRows, interval, from, nowUtc, stock.Exchange);
        var fallbackCoverage = ComputeCoverageRatio(fallbackRows, interval, from, nowUtc, stock.Exchange);
        var fallbackMateriallyImprovesCoverage = fallbackRows.Count > aggregateRows.Count
                                                 && fallbackCoverage >= aggregateCoverage + 0.15m;
        var fallbackIsNewer = fallbackRows[^1].Timestamp > aggregateRows[^1].Timestamp
                              && fallbackCoverage >= aggregateCoverage;

        return fallbackMateriallyImprovesCoverage || fallbackIsNewer
            ? (fallbackRows, true)
            : (aggregateRows, false);
    }

    private static decimal ComputeCoverageRatio(
        IReadOnlyList<StockHistoricalPrice> rows,
        string interval,
        DateTime from,
        DateTime nowUtc,
        string? exchange)
    {
        if (rows.Count == 0)
        {
            return 0m;
        }

        var expectedCount = EstimateExpectedBucketCount(interval, from, nowUtc, exchange);
        if (expectedCount <= 0)
        {
            return 1m;
        }

        return Math.Min(1m, rows.Count / (decimal)expectedCount);
    }

    private static int EstimateExpectedBucketCount(
        string interval,
        DateTime from,
        DateTime nowUtc,
        string? exchange)
    {
        TimeZoneInfo? timeZone = null;
        if (IsFrankfurtExchange(exchange)
            && TradingSessionCalendar.TryGetSessionSpec(StockExchanges.Frankfurt, out var sessionSpec))
        {
            timeZone = TradingSessionCalendar.TryResolveTimeZone(sessionSpec);
        }
        var fromDate = timeZone is null
            ? DateOnly.FromDateTime(from.Date)
            : TradingSessionCalendar.ConvertUtcToLocalDate(from, timeZone);
        var currentDate = timeZone is null
            ? DateOnly.FromDateTime(nowUtc.Date)
            : TradingSessionCalendar.ConvertUtcToLocalDate(nowUtc, timeZone);

        if (string.Equals(interval, "1wk", StringComparison.Ordinal))
        {
            var firstWeek = GetIsoWeekStart(fromDate);
            var currentWeek = GetIsoWeekStart(currentDate);
            var lastCompletedWeek = currentWeek.AddDays(-7);
            if (lastCompletedWeek < firstWeek)
            {
                return 0;
            }

            return ((lastCompletedWeek.DayNumber - firstWeek.DayNumber) / 7) + 1;
        }

        var firstMonth = new DateOnly(fromDate.Year, fromDate.Month, 1);
        var currentMonth = new DateOnly(currentDate.Year, currentDate.Month, 1);
        var lastCompletedMonth = currentMonth.AddMonths(-1);
        if (lastCompletedMonth < firstMonth)
        {
            return 0;
        }

        return ((lastCompletedMonth.Year - firstMonth.Year) * 12) + (lastCompletedMonth.Month - firstMonth.Month) + 1;
    }

    private static List<StockHistoricalPrice> AggregateDailyFromRows(
        int stockId,
        IReadOnlyList<StockHistoricalPrice> dailyRows,
        string targetInterval,
        DateTime nowUtc,
        string? exchange)
    {
        return IsFrankfurtExchange(exchange)
            ? AggregateFrankfurtFromDailyRows(stockId, dailyRows, targetInterval, nowUtc)
            : AggregateUtcFromDailyRows(stockId, dailyRows, targetInterval, nowUtc);
    }

    private static CandleBatch AggregateDailyBatch(
        CandleBatch dailyBatch,
        string targetInterval,
        DateTime nowUtc,
        string? exchange)
    {
        return IsFrankfurtExchange(exchange)
            ? AggregateFrankfurtDailyBatch(dailyBatch, targetInterval, nowUtc)
            : AggregateUtcDailyBatch(dailyBatch, targetInterval, nowUtc);
    }

    private static List<StockHistoricalPrice> AggregateUtcFromDailyRows(
        int stockId,
        IReadOnlyList<StockHistoricalPrice> dailyRows,
        string targetInterval,
        DateTime nowUtc)
    {
        var dedupedRows = dailyRows
            .Where(x => !x.IsQuoteDerived)
            .GroupBy(x => DateOnly.FromDateTime(x.Timestamp.Date))
            .Select(g => g.OrderBy(x => x.Timestamp).Last())
            .OrderBy(x => x.Timestamp)
            .ToList();
        if (dedupedRows.Count == 0)
        {
            return new List<StockHistoricalPrice>();
        }

        var currentUtcDate = DateOnly.FromDateTime(nowUtc.Date);
        var currentWeekStart = GetIsoWeekStart(currentUtcDate);
        var bucketed = targetInterval == "1wk"
            ? dedupedRows.GroupBy(x => GetIsoWeekStart(DateOnly.FromDateTime(x.Timestamp.Date)))
            : dedupedRows.GroupBy(x =>
            {
                var date = DateOnly.FromDateTime(x.Timestamp.Date);
                return new DateOnly(date.Year, date.Month, 1);
            });

        var result = new List<StockHistoricalPrice>();
        foreach (var bucket in bucketed.OrderBy(x => x.Key))
        {
            var isCompleted = targetInterval == "1wk"
                ? bucket.Key < currentWeekStart
                : bucket.Key.Year < currentUtcDate.Year
                  || (bucket.Key.Year == currentUtcDate.Year && bucket.Key.Month < currentUtcDate.Month);
            if (!isCompleted)
            {
                continue;
            }

            var ordered = bucket.OrderBy(x => x.Timestamp).ToList();
            if (ordered.Count == 0 || ordered.Any(x => x.Volume < 0))
            {
                continue;
            }

            long totalVolume;
            try
            {
                totalVolume = 0L;
                foreach (var row in ordered)
                {
                    checked
                    {
                        totalVolume += row.Volume;
                    }
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            var first = ordered[0];
            var last = ordered[^1];
            result.Add(new StockHistoricalPrice
            {
                StockId = stockId,
                Interval = targetInterval,
                Timestamp = DateTime.SpecifyKind(bucket.Key.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                Open = first.Open,
                High = ordered.Max(x => x.High),
                Low = ordered.Min(x => x.Low),
                Close = last.Close,
                AdjustedClose = last.AdjustedClose,
                QuoteCurrency = last.QuoteCurrency,
                FinancialCurrency = last.FinancialCurrency,
                NormalizedQuoteCurrency = last.NormalizedQuoteCurrency,
                QuoteUnitMultiplier = last.QuoteUnitMultiplier,
                Volume = totalVolume,
                IsQuoteDerived = false,
            });
        }

        return result;
    }

    private static CandleBatch AggregateUtcDailyBatch(CandleBatch dailyBatch, string targetInterval, DateTime nowUtc)
    {
        var dedupedCandles = dailyBatch.Candles
            .GroupBy(x => DateOnly.FromDateTime(x.Timestamp.Date))
            .Select(g => g.OrderBy(x => x.Timestamp).Last())
            .OrderBy(x => x.Timestamp)
            .ToList();
        if (dedupedCandles.Count == 0)
        {
            return new CandleBatch(
                new List<CandleData>(),
                dailyBatch.QuoteCurrency,
                dailyBatch.FinancialCurrency,
                dailyBatch.NormalizedQuoteCurrency,
                dailyBatch.QuoteUnitMultiplier);
        }

        var currentUtcDate = DateOnly.FromDateTime(nowUtc.Date);
        var currentWeekStart = GetIsoWeekStart(currentUtcDate);
        var bucketed = targetInterval == "1wk"
            ? dedupedCandles.GroupBy(x => GetIsoWeekStart(DateOnly.FromDateTime(x.Timestamp.Date)))
            : dedupedCandles.GroupBy(x =>
            {
                var date = DateOnly.FromDateTime(x.Timestamp.Date);
                return new DateOnly(date.Year, date.Month, 1);
            });

        var aggregated = new List<CandleData>();
        foreach (var bucket in bucketed.OrderBy(x => x.Key))
        {
            var isCompleted = targetInterval == "1wk"
                ? bucket.Key < currentWeekStart
                : bucket.Key.Year < currentUtcDate.Year
                  || (bucket.Key.Year == currentUtcDate.Year && bucket.Key.Month < currentUtcDate.Month);
            if (!isCompleted)
            {
                continue;
            }

            var ordered = bucket.OrderBy(x => x.Timestamp).ToList();
            if (ordered.Count == 0 || ordered.Any(x => x.Volume < 0))
            {
                continue;
            }

            long totalVolume;
            try
            {
                totalVolume = 0L;
                foreach (var candle in ordered)
                {
                    checked
                    {
                        totalVolume += candle.Volume;
                    }
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            var first = ordered[0];
            var last = ordered[^1];
            aggregated.Add(new CandleData(
                DateTime.SpecifyKind(bucket.Key.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                first.Open,
                ordered.Max(x => x.High),
                ordered.Min(x => x.Low),
                last.Close,
                last.AdjustedClose,
                totalVolume));
        }

        return new CandleBatch(
            aggregated,
            dailyBatch.QuoteCurrency,
            dailyBatch.FinancialCurrency,
            dailyBatch.NormalizedQuoteCurrency,
            dailyBatch.QuoteUnitMultiplier);
    }

    private static List<StockHistoricalPrice> AggregateFrankfurtFromDailyRows(
        int stockId,
        IReadOnlyList<StockHistoricalPrice> dailyRows,
        string targetInterval,
        DateTime nowUtc)
    {
        if (dailyRows.Count == 0 ||
            !TradingSessionCalendar.TryGetSessionSpec(StockExchanges.Frankfurt, out var sessionSpec))
        {
            return new List<StockHistoricalPrice>();
        }

        var berlinTimeZone = TradingSessionCalendar.TryResolveTimeZone(sessionSpec);
        if (berlinTimeZone is null)
        {
            return new List<StockHistoricalPrice>();
        }

        var dedupedRows = dailyRows
            .Where(x => !x.IsQuoteDerived)
            .GroupBy(x => TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone))
            .Select(g => g
                .OrderBy(x => x.Timestamp)
                .Last())
            .OrderBy(x => x.Timestamp)
            .ToList();

        if (dedupedRows.Count == 0)
        {
            return new List<StockHistoricalPrice>();
        }

        var currentLocalDate = TradingSessionCalendar.ConvertUtcToLocalDate(nowUtc, berlinTimeZone);
        var currentWeekStart = GetIsoWeekStart(currentLocalDate);

        var bucketed = targetInterval == "1wk"
            ? dedupedRows.GroupBy(x => GetIsoWeekStart(TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone)))
            : dedupedRows.GroupBy(x =>
            {
                var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone);
                return new DateOnly(localDate.Year, localDate.Month, 1);
            });

        var result = new List<StockHistoricalPrice>();
        foreach (var bucket in bucketed.OrderBy(x => x.Key))
        {
            var isCompleted = targetInterval == "1wk"
                ? bucket.Key < currentWeekStart
                : bucket.Key.Year < currentLocalDate.Year
                  || (bucket.Key.Year == currentLocalDate.Year && bucket.Key.Month < currentLocalDate.Month);
            if (!isCompleted)
            {
                continue;
            }

            var ordered = bucket.OrderBy(x => x.Timestamp).ToList();
            if (ordered.Count == 0 || ordered.Any(x => x.Volume < 0))
            {
                continue;
            }

            long totalVolume;
            try
            {
                totalVolume = 0L;
                foreach (var row in ordered)
                {
                    checked
                    {
                        totalVolume += row.Volume;
                    }
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            var first = ordered[0];
            var last = ordered[^1];
            result.Add(new StockHistoricalPrice
            {
                StockId = stockId,
                Interval = targetInterval,
                Timestamp = ConvertLocalDateStartToUtc(bucket.Key, berlinTimeZone),
                Open = first.Open,
                High = ordered.Max(x => x.High),
                Low = ordered.Min(x => x.Low),
                Close = last.Close,
                AdjustedClose = last.AdjustedClose,
                QuoteCurrency = last.QuoteCurrency,
                FinancialCurrency = last.FinancialCurrency,
                NormalizedQuoteCurrency = last.NormalizedQuoteCurrency,
                QuoteUnitMultiplier = last.QuoteUnitMultiplier,
                Volume = totalVolume,
                IsQuoteDerived = false,
            });
        }

        return result;
    }

    private static CandleBatch AggregateFrankfurtDailyBatch(CandleBatch dailyBatch, string targetInterval, DateTime nowUtc)
    {
        if (dailyBatch.Candles.Count == 0 ||
            !TradingSessionCalendar.TryGetSessionSpec(StockExchanges.Frankfurt, out var sessionSpec))
        {
            return new CandleBatch(
                new List<CandleData>(),
                dailyBatch.QuoteCurrency,
                dailyBatch.FinancialCurrency,
                dailyBatch.NormalizedQuoteCurrency,
                dailyBatch.QuoteUnitMultiplier);
        }

        var berlinTimeZone = TradingSessionCalendar.TryResolveTimeZone(sessionSpec);
        if (berlinTimeZone is null)
        {
            return new CandleBatch(
                new List<CandleData>(),
                dailyBatch.QuoteCurrency,
                dailyBatch.FinancialCurrency,
                dailyBatch.NormalizedQuoteCurrency,
                dailyBatch.QuoteUnitMultiplier);
        }

        var dedupedCandles = dailyBatch.Candles
            .GroupBy(x => TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone))
            .Select(g => g.OrderBy(x => x.Timestamp).Last())
            .OrderBy(x => x.Timestamp)
            .ToList();

        var currentLocalDate = TradingSessionCalendar.ConvertUtcToLocalDate(nowUtc, berlinTimeZone);
        var currentWeekStart = GetIsoWeekStart(currentLocalDate);

        var bucketed = targetInterval == "1wk"
            ? dedupedCandles.GroupBy(x => GetIsoWeekStart(TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone)))
            : dedupedCandles.GroupBy(x =>
            {
                var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(x.Timestamp, berlinTimeZone);
                return new DateOnly(localDate.Year, localDate.Month, 1);
            });

        var aggregated = new List<CandleData>();
        foreach (var bucket in bucketed.OrderBy(x => x.Key))
        {
            var isCompleted = targetInterval == "1wk"
                ? bucket.Key < currentWeekStart
                : bucket.Key.Year < currentLocalDate.Year
                  || (bucket.Key.Year == currentLocalDate.Year && bucket.Key.Month < currentLocalDate.Month);
            if (!isCompleted)
            {
                continue;
            }

            var ordered = bucket.OrderBy(x => x.Timestamp).ToList();
            if (ordered.Count == 0 || ordered.Any(x => x.Volume < 0))
            {
                continue;
            }

            long totalVolume;
            try
            {
                totalVolume = 0L;
                foreach (var candle in ordered)
                {
                    checked
                    {
                        totalVolume += candle.Volume;
                    }
                }
            }
            catch (OverflowException)
            {
                continue;
            }

            var first = ordered[0];
            var last = ordered[^1];
            aggregated.Add(new CandleData(
                ConvertLocalDateStartToUtc(bucket.Key, berlinTimeZone),
                first.Open,
                ordered.Max(x => x.High),
                ordered.Min(x => x.Low),
                last.Close,
                last.AdjustedClose,
                totalVolume));
        }

        return new CandleBatch(
            aggregated,
            dailyBatch.QuoteCurrency,
            dailyBatch.FinancialCurrency,
            dailyBatch.NormalizedQuoteCurrency,
            dailyBatch.QuoteUnitMultiplier);
    }

    private static DateOnly GetIsoWeekStart(DateOnly date)
    {
        var day = date.DayOfWeek;
        var delta = day == DayOfWeek.Sunday ? 6 : (int)day - 1;
        return date.AddDays(-delta);
    }

    private static DateTime ConvertLocalDateStartToUtc(DateOnly localDate, TimeZoneInfo timeZone)
    {
        var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
    }

    private async Task<List<StockHistoricalPrice>> LoadHistoryRowsAsync(int stockId, string interval, DateTime from, CancellationToken cancellationToken)
    {
        return await _dbContext.StockHistoricalPrices
            .AsNoTracking()
            .Where(x => x.StockId == stockId && x.Interval == interval && x.Timestamp >= from)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(cancellationToken);
    }

    private static StockHistoryVolumeMetricsResponse BuildVolumeMetrics(
        IReadOnlyList<StockHistoricalPrice> data,
        string interval,
        CurrencyConversionContext conversionContext,
        bool latestPointIsCompletedByConstruction = false)
    {
        if (data.Count == 0)
        {
            return new StockHistoryVolumeMetricsResponse();
        }

        var latestContext = latestPointIsCompletedByConstruction
            ? new LatestMetricsContext(data[^1], data.Count - 1, true)
            : ResolveLatestMetricsPoint(data, interval);
        if (latestContext.Point is null)
        {
            return new StockHistoryVolumeMetricsResponse
            {
                UsesCompletedCandle = latestContext.UsesCompletedCandle
            };
        }

        var latestIndex = latestContext.Index;
        var averageVolume20 = TryCalculateAverageVolume(data, latestIndex, 20);
        var averageVolume50 = TryCalculateAverageVolume(data, latestIndex, 50);
        var latestVolume = latestContext.Point.Volume > 0 ? latestContext.Point.Volume : (long?)null;

        var closeNormalized = conversionContext.Normalize(latestContext.Point.Close);
        var closeForTurnover = conversionContext.ConvertNormalizedToEur(closeNormalized) ?? closeNormalized;
        var turnoverCurrency = conversionContext.ExchangeRate.RateToEur is not null
            ? "EUR"
            : conversionContext.Metadata.NormalizedQuoteCurrency ?? conversionContext.Metadata.QuoteCurrency;

        decimal? turnover = null;
        if (latestVolume is > 0 && closeForTurnover > 0m)
        {
            turnover = closeForTurnover * latestVolume.Value;
        }

        decimal? relativeVolume = null;
        if (latestVolume is > 0 && averageVolume20 is > 0m)
        {
            relativeVolume = latestVolume.Value / averageVolume20.Value;
        }

        return new StockHistoryVolumeMetricsResponse
        {
            AverageVolume20 = averageVolume20,
            AverageVolume50 = averageVolume50,
            RelativeVolume = relativeVolume,
            Turnover = turnover,
            TurnoverCurrency = turnoverCurrency,
            LatestMetricsTimestamp = latestContext.Point.Timestamp,
            UsesCompletedCandle = latestContext.UsesCompletedCandle
        };
    }

    private static decimal? TryCalculateAverageVolume(IReadOnlyList<StockHistoricalPrice> data, int latestIndex, int periods)
    {
        var startIndex = latestIndex - periods + 1;
        if (startIndex < 0)
        {
            return null;
        }

        decimal totalVolume = 0m;
        for (var i = startIndex; i <= latestIndex; i++)
        {
            totalVolume += data[i].Volume;
        }

        var average = totalVolume / periods;
        return average > 0m ? average : null;
    }

    private static LatestMetricsContext ResolveLatestMetricsPoint(IReadOnlyList<StockHistoricalPrice> data, string interval)
    {
        if (data.Count == 0)
        {
            return new LatestMetricsContext(null, -1, false);
        }

        if (TryGetIntradayIntervalDuration(interval, out var intervalDuration))
        {
            var now = DateTime.UtcNow;
            for (var i = data.Count - 1; i >= 0; i--)
            {
                if ((data[i].Timestamp + intervalDuration) <= now)
                {
                    return new LatestMetricsContext(data[i], i, true);
                }
            }

            return new LatestMetricsContext(null, -1, true);
        }

        if (string.Equals(interval, "1d", StringComparison.Ordinal))
        {
            var today = DateTime.UtcNow.Date;
            for (var i = data.Count - 1; i >= 0; i--)
            {
                if (data[i].Timestamp.Date < today)
                {
                    return new LatestMetricsContext(data[i], i, true);
                }
            }
        }

        var latestIndex = data.Count - 1;
        return new LatestMetricsContext(data[latestIndex], latestIndex, false);
    }

    private static bool TryGetIntradayIntervalDuration(string interval, out TimeSpan duration)
    {
        switch (interval)
        {
            case "10m":
                duration = TimeSpan.FromMinutes(10);
                return true;
            case "1h":
                duration = TimeSpan.FromHours(1);
                return true;
            default:
                duration = default;
                return false;
        }
    }

    private static bool NeedsMetadataBackfill(StockHistoricalPrice price) =>
        string.IsNullOrWhiteSpace(price.QuoteCurrency) ||
        string.IsNullOrWhiteSpace(price.NormalizedQuoteCurrency) ||
        price.QuoteUnitMultiplier <= 0m;

    private bool ShouldRefreshIntradayOnDemand(
        Stock stock,
        string normalizedRange,
        string interval,
        IReadOnlyList<StockHistoricalPrice> data,
        DateTime nowUtc)
    {
        if (!string.Equals(interval, "10m", StringComparison.Ordinal) ||
            normalizedRange is not ("24h" or "today"))
        {
            return false;
        }

        if (data.Count == 0)
        {
            return true;
        }

        var asOfUtc = data[^1].Timestamp;
        if (!IsPotentiallyStale(interval, asOfUtc, nowUtc))
        {
            return false;
        }

        if (!IsLikelyIntradaySessionOpen(stock.Exchange, nowUtc))
        {
            return false;
        }

        return TryReserveOnDemandIntradayRefresh(stock.Id, nowUtc);
    }

    private bool TryReserveOnDemandIntradayRefresh(int stockId, DateTime nowUtc)
    {
        while (true)
        {
            if (!LastOnDemandIntradayRefreshAttemptUtc.TryGetValue(stockId, out var previousAttemptUtc))
            {
                if (LastOnDemandIntradayRefreshAttemptUtc.TryAdd(stockId, nowUtc))
                {
                    return true;
                }

                continue;
            }

            if (nowUtc <= previousAttemptUtc)
            {
                if (LastOnDemandIntradayRefreshAttemptUtc.TryUpdate(stockId, nowUtc, previousAttemptUtc))
                {
                    return true;
                }

                continue;
            }

            if ((nowUtc - previousAttemptUtc) < _options.OnDemandIntradayRefreshMinInterval)
            {
                return false;
            }

            if (LastOnDemandIntradayRefreshAttemptUtc.TryUpdate(stockId, nowUtc, previousAttemptUtc))
            {
                return true;
            }
        }
    }

    private static bool IsLikelyIntradaySessionOpen(string? exchange, DateTime nowUtc)
    {
        if (!TradingSessionCalendar.TryGetSessionSpec(exchange, out var spec))
        {
            return true;
        }

        var timeZone = TradingSessionCalendar.TryResolveTimeZone(spec);
        if (timeZone is null)
        {
            return true;
        }

        if (!TradingSessionCalendar.IsWithinRegularSession(nowUtc, spec, timeZone, out _))
        {
            return false;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        return localNow.TimeOfDay <= spec.CloseLocalTime.Add(TimeSpan.FromMinutes(20));
    }

    private static string ResolveStockProviderSymbol(Stock stock)
    {
        var persistedProviderSymbol = stock.ProviderSymbol?.Trim();
        if (!string.IsNullOrWhiteSpace(persistedProviderSymbol))
        {
            return persistedProviderSymbol;
        }

        return StockExchanges.ResolveProviderSymbol(stock.Ticker, stock.Exchange);
    }

    private static string? BuildHistoryStaleReason(
        string? exchange,
        string normalizedRange,
        string interval,
        DateTime? asOfUtc,
        DateTime nowUtc,
        bool isPotentiallyStale,
        bool onDemandRefreshFailed)
    {
        if (!isPotentiallyStale && !onDemandRefreshFailed)
        {
            return null;
        }

        var rangeLabel = normalizedRange switch
        {
            "today" => "Сегодня",
            "24h" => "24 ч.",
            _ => normalizedRange
        };
        var asOfText = asOfUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "недоступно";

        if (onDemandRefreshFailed && isPotentiallyStale)
        {
            return $"Данные за диапазон «{rangeLabel}» могут быть устаревшими (по состоянию на {asOfText}). Последнее обновление не удалось, показаны сохранённые свечи.";
        }

        if (onDemandRefreshFailed)
        {
            return "Последнее обновление истории не удалось, отображены сохранённые свечи.";
        }

        if (string.Equals(interval, "10m", StringComparison.Ordinal) && !IsLikelyIntradaySessionOpen(exchange, nowUtc))
        {
            return $"Рынок сейчас закрыт. Показаны последние доступные внутридневные свечи (по состоянию на {asOfText}).";
        }

        return $"Данные за диапазон «{rangeLabel}» могут быть устаревшими (по состоянию на {asOfText}).";
    }

    private static string BuildUnavailableReason(Stock stock, string normalizedRange, bool onDemandRefreshFailed)
    {
        if (onDemandRefreshFailed)
        {
            return "История временно недоступна из-за ошибки источника данных или лимита запросов. Повторите попытку позже или запустите «Перезагрузить историю».";
        }

        var listingLabel = string.IsNullOrWhiteSpace(stock.Exchange)
            ? stock.Ticker
            : $"{stock.Ticker} ({stock.Exchange})";
        return $"Для листинга {listingLabel} нет данных за диапазон «{normalizedRange}». Проверьте биржу/тикер и попробуйте «Перезагрузить историю».";
    }

    private async Task<List<Stock>> LoadDueAutomaticStocksAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var dueBySchedule = await _dbContext.Stocks
            .Where(s =>
                s.HistoryRefreshCadence != StockHistoryRefreshCadence.Disabled &&
                !string.IsNullOrWhiteSpace(s.Ticker) &&
                (
                    (s.NextFullHistoryBackfillAtUtc.HasValue && s.NextFullHistoryBackfillAtUtc <= nowUtc) ||
                    (s.NextHistoryReconciliationAtUtc.HasValue && s.NextHistoryReconciliationAtUtc <= nowUtc) ||
                    (s.NextIncrementalHistoryRefreshAtUtc.HasValue && s.NextIncrementalHistoryRefreshAtUtc <= nowUtc)
                ))
            .OrderBy(s => s.Id)
            .Take(_options.MaxAutomaticStocksPerRun)
            .ToListAsync(cancellationToken);

        if (dueBySchedule.Count >= _options.MaxAutomaticStocksPerRun)
        {
            return dueBySchedule;
        }

        var stockIdsWithDailyHistory = await _dbContext.StockHistoricalPrices
            .AsNoTracking()
            .Where(x => x.Interval == "1d")
            .Select(x => x.StockId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var remaining = _options.MaxAutomaticStocksPerRun - dueBySchedule.Count;
        var noHistoryStocks = await _dbContext.Stocks
            .Where(s =>
                s.HistoryRefreshCadence != StockHistoryRefreshCadence.Disabled &&
                !string.IsNullOrWhiteSpace(s.Ticker) &&
                !stockIdsWithDailyHistory.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Take(remaining)
            .ToListAsync(cancellationToken);

        return dueBySchedule
            .Concat(noHistoryStocks)
            .GroupBy(x => x.Id)
            .Select(g => g.First())
            .OrderBy(x => x.Id)
            .ToList();
    }

    private static bool IsPotentiallyStale(string interval, DateTime? asOfUtc, DateTime nowUtc)
    {
        if (asOfUtc is null)
        {
            return true;
        }

        var threshold = interval switch
        {
            "10m" => TimeSpan.FromHours(2),
            "1h" => TimeSpan.FromHours(8),
            "1d" => TimeSpan.FromDays(4),
            "1wk" => TimeSpan.FromDays(14),
            _ => TimeSpan.FromDays(45),
        };

        return (nowUtc - asOfUtc.Value) > threshold;
    }

    private static void EnsureCadenceDefault(Stock stock)
    {
        if (Enum.IsDefined(stock.HistoryRefreshCadence))
        {
            return;
        }

        stock.HistoryRefreshCadence = stock.TrackingStatus == StockTrackingStatus.CatalogOnly
            ? StockHistoryRefreshCadence.Weekly
            : StockHistoryRefreshCadence.Daily;
    }

    private StockHistoryRefreshTier? ResolveTier(Stock stock, bool hasHistory, StockHistoryRefreshTrigger trigger, DateTime nowUtc)
    {
        if (trigger == StockHistoryRefreshTrigger.Manual)
        {
            return StockHistoryRefreshTier.FullBackfill;
        }

        if (stock.HistoryRefreshCadence == StockHistoryRefreshCadence.Disabled)
        {
            return null;
        }

        if (!hasHistory)
        {
            return StockHistoryRefreshTier.FullBackfill;
        }

        if (stock.NextFullHistoryBackfillAtUtc is null || stock.NextFullHistoryBackfillAtUtc <= nowUtc)
        {
            return StockHistoryRefreshTier.FullBackfill;
        }

        if (stock.NextHistoryReconciliationAtUtc is null || stock.NextHistoryReconciliationAtUtc <= nowUtc)
        {
            return StockHistoryRefreshTier.Reconciliation;
        }

        if (stock.NextIncrementalHistoryRefreshAtUtc is null || stock.NextIncrementalHistoryRefreshAtUtc <= nowUtc)
        {
            return StockHistoryRefreshTier.Incremental;
        }

        return null;
    }

    private void MarkTierSuccess(Stock stock, StockHistoryRefreshTier tier, DateTime nowUtc)
    {
        switch (tier)
        {
            case StockHistoryRefreshTier.Incremental:
                stock.LastIncrementalHistoryRefreshSucceededAtUtc = nowUtc;
                stock.NextIncrementalHistoryRefreshAtUtc = nowUtc + GetIncrementalCadence(stock);
                break;
            case StockHistoryRefreshTier.Reconciliation:
                stock.LastHistoryReconciliationSucceededAtUtc = nowUtc;
                stock.NextHistoryReconciliationAtUtc = nowUtc + GetReconciliationCadence(stock);
                if (stock.NextIncrementalHistoryRefreshAtUtc is null || stock.NextIncrementalHistoryRefreshAtUtc < nowUtc)
                {
                    stock.NextIncrementalHistoryRefreshAtUtc = nowUtc + GetIncrementalCadence(stock);
                }
                break;
            case StockHistoryRefreshTier.FullBackfill:
                stock.LastFullHistoryBackfillSucceededAtUtc = nowUtc;
                stock.NextFullHistoryBackfillAtUtc = nowUtc + GetFullBackfillCadence(stock);
                if (stock.NextHistoryReconciliationAtUtc is null || stock.NextHistoryReconciliationAtUtc < nowUtc)
                {
                    stock.NextHistoryReconciliationAtUtc = nowUtc + GetReconciliationCadence(stock);
                }

                if (stock.NextIncrementalHistoryRefreshAtUtc is null || stock.NextIncrementalHistoryRefreshAtUtc < nowUtc)
                {
                    stock.NextIncrementalHistoryRefreshAtUtc = nowUtc + GetIncrementalCadence(stock);
                }
                break;
        }
    }

    private void ScheduleRetry(Stock stock, StockHistoryRefreshTier tier, DateTime nowUtc)
    {
        var retryAtUtc = nowUtc + _options.TransientFailureRetryDelay;
        switch (tier)
        {
            case StockHistoryRefreshTier.Incremental:
                stock.NextIncrementalHistoryRefreshAtUtc = retryAtUtc;
                break;
            case StockHistoryRefreshTier.Reconciliation:
                stock.NextHistoryReconciliationAtUtc = retryAtUtc;
                break;
            case StockHistoryRefreshTier.FullBackfill:
                stock.NextFullHistoryBackfillAtUtc = retryAtUtc;
                break;
        }
    }

    private DateTime? ComputeNextDueAtUtc(Stock stock, DateTime nowUtc)
    {
        if (stock.HistoryRefreshCadence == StockHistoryRefreshCadence.Disabled)
        {
            return null;
        }

        var due = new[] {
            stock.NextIncrementalHistoryRefreshAtUtc,
            stock.NextHistoryReconciliationAtUtc,
            stock.NextFullHistoryBackfillAtUtc
        }
        .Where(x => x.HasValue)
        .Select(x => x!.Value)
        .OrderBy(x => x)
        .FirstOrDefault();

        return due == default ? nowUtc : due;
    }

    private TimeSpan GetIncrementalCadence(Stock stock)
        => stock.HistoryRefreshCadence == StockHistoryRefreshCadence.Weekly
            ? _options.IncrementalWeeklyCadence
            : _options.IncrementalDailyCadence;

    private TimeSpan GetReconciliationCadence(Stock stock)
        => stock.TrackingStatus == StockTrackingStatus.CatalogOnly
            ? _options.ReconciliationCatalogCadence
            : _options.ReconciliationTrackedCadence;

    private TimeSpan GetFullBackfillCadence(Stock stock)
        => stock.TrackingStatus == StockTrackingStatus.CatalogOnly
            ? _options.FullBackfillCatalogCadence
            : _options.FullBackfillTrackedCadence;

    private async Task UpsertCandlesAsync(int stockId, string interval, CandleBatch candleBatch, CancellationToken cancellationToken)
    {
        var candles = candleBatch.Candles;
        if (candles.Count == 0)
        {
            return;
        }

        var minTimestamp = candles.Min(x => x.Timestamp);
        var maxTimestamp = candles.Max(x => x.Timestamp);

        var existing = await _dbContext.StockHistoricalPrices
            .Where(x =>
                x.StockId == stockId &&
                x.Interval == interval &&
                x.Timestamp >= minTimestamp &&
                x.Timestamp <= maxTimestamp)
            .ToListAsync(cancellationToken);

        var existingByTimestamp = existing.ToDictionary(x => x.Timestamp, x => x);

        foreach (var candle in candles)
        {
            if (existingByTimestamp.TryGetValue(candle.Timestamp, out var row))
            {
                row.Open = candle.Open;
                row.High = candle.High;
                row.Low = candle.Low;
                row.Close = candle.Close;
                row.AdjustedClose = candle.AdjustedClose;
                row.QuoteCurrency = candleBatch.QuoteCurrency;
                row.FinancialCurrency = candleBatch.FinancialCurrency;
                row.NormalizedQuoteCurrency = candleBatch.NormalizedQuoteCurrency;
                row.QuoteUnitMultiplier = candleBatch.QuoteUnitMultiplier;
                row.Volume = candle.Volume;
                row.IsQuoteDerived = false;
            }
            else
            {
                _dbContext.StockHistoricalPrices.Add(new StockHistoricalPrice
                {
                    StockId = stockId,
                    Timestamp = candle.Timestamp,
                    Interval = interval,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    AdjustedClose = candle.AdjustedClose,
                    QuoteCurrency = candleBatch.QuoteCurrency,
                    FinancialCurrency = candleBatch.FinancialCurrency,
                    NormalizedQuoteCurrency = candleBatch.NormalizedQuoteCurrency,
                    QuoteUnitMultiplier = candleBatch.QuoteUnitMultiplier,
                    Volume = candle.Volume,
                    IsQuoteDerived = false,
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<CandleFetchResult> FetchCandlesByDateRangeAsync(
        string symbol,
        string interval,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var period1 = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        if (period2 <= period1)
        {
            period2 = period1 + 3600;
        }

        var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval={interval}&period1={period1}&period2={period2}";
        var requestLabel = $"history:{interval}:{period1}-{period2}";
        return await FetchCandlesInternalAsync(url, requestLabel, interval, $"{fromUtc:yyyy-MM-dd}..{toUtc:yyyy-MM-dd}", cancellationToken);
    }

    private async Task<CandleFetchResult> FetchCandlesAsync(string symbol, string interval, string range, CancellationToken cancellationToken)
    {
        var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval={interval}&range={range}";
        var requestLabel = $"history:{interval}:{range}";
        return await FetchCandlesInternalAsync(url, requestLabel, interval, range, cancellationToken);
    }

    private async Task<CandleFetchResult> FetchCandlesInternalAsync(
        string url,
        string requestLabel,
        string interval,
        string rangeLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _yahooRequestCoordinator.GetAsync(
                url,
                requestLabel,
                new YahooRequestExecutionOptions(
                    MaxYahooRequestAttempts,
                    YahooRetryBaseDelay,
                    YahooRetryMaxDelay),
                cancellationToken);

            if (response.IsRateLimited)
            {
                _logger.LogWarning(
                    "Yahoo history request rate limited for interval={Interval} range={Range}; cooldownUntilUtc={CooldownUntilUtc}",
                    interval,
                    rangeLabel,
                    response.CooldownUntilUtc);
                return CandleFetchResult.RateLimited();
            }

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning(
                    "Yahoo history request failed for interval={Interval} range={Range}: {StatusCode}",
                    interval,
                    rangeLabel,
                    (int)response.StatusCode);
                return CandleFetchResult.Success(CandleBatch.Empty);
            }

            using var doc = JsonDocument.Parse(response.Content);
            return CandleFetchResult.Success(ParseCandles(doc.RootElement));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Yahoo history request timed out for interval={Interval} range={Range}", interval, rangeLabel);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Yahoo history request failed for interval={Interval} range={Range}", interval, rangeLabel);
            throw;
        }
    }

    private static CandleBatch ParseCandles(JsonElement root)
    {
        if (!root.TryGetProperty("chart", out var chart) ||
            !chart.TryGetProperty("result", out var resultArray) ||
            resultArray.GetArrayLength() == 0)
        {
            return CandleBatch.Empty;
        }

        var result = resultArray[0];
        var meta = result.TryGetProperty("meta", out var metaElement) ? metaElement : default;
        if (!result.TryGetProperty("timestamp", out var timestamps) ||
            !result.TryGetProperty("indicators", out var indicators) ||
            !indicators.TryGetProperty("quote", out var quoteArray) ||
            quoteArray.GetArrayLength() == 0)
        {
            return CandleBatch.Empty;
        }

        var quote = quoteArray[0];
        // Yahoo quote.close remains the canonical raw/unadjusted close stored in StockHistoricalPrice.Close.
        // Where indicators.adjclose is present and aligned, we additionally persist it into
        // StockHistoricalPrice.AdjustedClose for split/dividend-aware close-based analytics.
        // Raw OHLC remains unchanged because Yahoo does not expose adjusted OHLC in this model.
        if (!quote.TryGetProperty("close", out var closeArray))
        {
            return CandleBatch.Empty;
        }

        JsonElement adjustedCloseArray = default;
        if (indicators.TryGetProperty("adjclose", out var adjCloseWrapperArray) &&
            adjCloseWrapperArray.ValueKind == JsonValueKind.Array &&
            adjCloseWrapperArray.GetArrayLength() > 0)
        {
            var adjustedCloseWrapper = adjCloseWrapperArray[0];
            if (adjustedCloseWrapper.ValueKind == JsonValueKind.Object &&
                adjustedCloseWrapper.TryGetProperty("adjclose", out var parsedAdjustedCloseArray) &&
                parsedAdjustedCloseArray.ValueKind == JsonValueKind.Array &&
                parsedAdjustedCloseArray.GetArrayLength() == timestamps.GetArrayLength())
            {
                adjustedCloseArray = parsedAdjustedCloseArray;
            }
        }

        var openArray = quote.TryGetProperty("open", out var openElement) ? openElement : default;
        var highArray = quote.TryGetProperty("high", out var highElement) ? highElement : default;
        var lowArray = quote.TryGetProperty("low", out var lowElement) ? lowElement : default;
        var volumeArray = quote.TryGetProperty("volume", out var volumeElement) ? volumeElement : default;

        var candles = new List<CandleData>();
        var pointsCount = timestamps.GetArrayLength();
        for (var i = 0; i < pointsCount; i++)
        {
            if (!TryGetInt64(timestamps, i, out var unixTimestamp))
            {
                continue;
            }

            if (!TryGetDecimal(closeArray, i, out var close))
            {
                continue;
            }

            var open = TryGetDecimal(openArray, i, out var parsedOpen) ? parsedOpen : close;
            var high = TryGetDecimal(highArray, i, out var parsedHigh) ? parsedHigh : close;
            var low = TryGetDecimal(lowArray, i, out var parsedLow) ? parsedLow : close;
            var volume = TryGetInt64(volumeArray, i, out var parsedVolume) ? parsedVolume : 0L;
            decimal? adjustedClose = TryGetAdjustedClose(adjustedCloseArray, i, out var parsedAdjustedClose)
                ? parsedAdjustedClose
                : null;

            candles.Add(new CandleData(
                DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime,
                open,
                high,
                low,
                close,
                adjustedClose,
                volume));
        }

        var quoteCurrency = meta.TryGetProperty("currency", out var currencyProp) ? currencyProp.GetString() : null;
        var financialCurrency = meta.TryGetProperty("financialCurrency", out var financialCurrencyProp) ? financialCurrencyProp.GetString() : null;
        var currencyMetadata = QuoteCurrencyMetadata.Parse(quoteCurrency, financialCurrency);

        return new CandleBatch(
            candles.OrderBy(x => x.Timestamp).ToList(),
            currencyMetadata.QuoteCurrency,
            currencyMetadata.FinancialCurrency,
            currencyMetadata.NormalizedQuoteCurrency,
            currencyMetadata.QuoteUnitMultiplier);
    }

    private static CandleBatch AggregateToTenMinute(CandleBatch fiveMinuteCandles)
    {
        var aggregatedCandles = fiveMinuteCandles.Candles
            .GroupBy(x => new DateTime(
                x.Timestamp.Year,
                x.Timestamp.Month,
                x.Timestamp.Day,
                x.Timestamp.Hour,
                (x.Timestamp.Minute / 10) * 10,
                0,
                DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.Timestamp).ToList();
                return new CandleData(
                    g.Key,
                    ordered.First().Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    ordered.Last().Close,
                    null,
                    ordered.Sum(x => x.Volume));
            })
            .ToList();

        return new CandleBatch(
            aggregatedCandles,
            fiveMinuteCandles.QuoteCurrency,
            fiveMinuteCandles.FinancialCurrency,
            fiveMinuteCandles.NormalizedQuoteCurrency,
            fiveMinuteCandles.QuoteUnitMultiplier);
    }

    private static bool TryGetDecimal(JsonElement arrayElement, int index, out decimal value)
    {
        value = 0m;
        if (arrayElement.ValueKind != JsonValueKind.Array || index < 0 || index >= arrayElement.GetArrayLength())
        {
            return false;
        }

        var element = arrayElement[index];
        if (element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        try
        {
            if (element.TryGetDecimal(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                value = Convert.ToDecimal(element.GetDouble(), CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryGetInt64(JsonElement arrayElement, int index, out long value)
    {
        value = 0L;
        if (arrayElement.ValueKind != JsonValueKind.Array || index < 0 || index >= arrayElement.GetArrayLength())
        {
            return false;
        }

        var element = arrayElement[index];
        if (element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        try
        {
            if (element.TryGetInt64(out value))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                value = Convert.ToInt64(element.GetDouble());
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryGetAdjustedClose(JsonElement arrayElement, int index, out decimal value)
    {
        if (!TryGetDecimal(arrayElement, index, out value))
        {
            return false;
        }

        return value > 0m;
    }

    private static StockHistoryRefreshOptions NormalizeOptions(StockHistoryRefreshOptions raw)
    {
        return new StockHistoryRefreshOptions
        {
            IncrementalLookbackDays = raw.IncrementalLookbackDays > 0 ? raw.IncrementalLookbackDays : 10,
            ReconciliationLookbackDays = raw.ReconciliationLookbackDays > 0 ? raw.ReconciliationLookbackDays : 183,
            FullBackfillLookbackDays = raw.FullBackfillLookbackDays > 0 ? raw.FullBackfillLookbackDays : FullBackfillMinimumLookbackDays,
            IncrementalDailyCadence = raw.IncrementalDailyCadence > TimeSpan.Zero ? raw.IncrementalDailyCadence : TimeSpan.FromDays(1),
            IncrementalWeeklyCadence = raw.IncrementalWeeklyCadence > TimeSpan.Zero ? raw.IncrementalWeeklyCadence : TimeSpan.FromDays(7),
            ReconciliationTrackedCadence = raw.ReconciliationTrackedCadence > TimeSpan.Zero ? raw.ReconciliationTrackedCadence : TimeSpan.FromDays(7),
            ReconciliationCatalogCadence = raw.ReconciliationCatalogCadence > TimeSpan.Zero ? raw.ReconciliationCatalogCadence : TimeSpan.FromDays(30),
            FullBackfillTrackedCadence = raw.FullBackfillTrackedCadence > TimeSpan.Zero ? raw.FullBackfillTrackedCadence : TimeSpan.FromDays(30),
            FullBackfillCatalogCadence = raw.FullBackfillCatalogCadence > TimeSpan.Zero ? raw.FullBackfillCatalogCadence : TimeSpan.FromDays(30),
            TransientFailureRetryDelay = raw.TransientFailureRetryDelay > TimeSpan.Zero ? raw.TransientFailureRetryDelay : TimeSpan.FromHours(2),
            OnDemandIntradayRefreshMinInterval = raw.OnDemandIntradayRefreshMinInterval > TimeSpan.Zero ? raw.OnDemandIntradayRefreshMinInterval : TimeSpan.FromMinutes(10),
            MaxAutomaticStocksPerRun = raw.MaxAutomaticStocksPerRun > 0 ? raw.MaxAutomaticStocksPerRun : 100,
        };
    }

    private sealed class IntradaySessionRange
    {
        private readonly TradingSessionSpec _spec;
        private readonly TimeZoneInfo _timeZone;
        private readonly DateOnly _previousSessionDate;
        private readonly DateOnly _currentSessionDate;

        public IntradaySessionRange(
            TradingSessionSpec spec,
            TimeZoneInfo timeZone,
            TradingSessionWindow previousSession,
            TradingSessionWindow currentSession,
            DateTime queryFromUtc)
        {
            _spec = spec;
            _timeZone = timeZone;
            _previousSessionDate = previousSession.SessionDateLocal;
            _currentSessionDate = currentSession.SessionDateLocal;
            PreviousSessionStartUtc = previousSession.SessionStartUtc;
            PreviousSessionEndUtc = previousSession.SessionEndUtc;
            CurrentSessionStartUtc = currentSession.SessionStartUtc;
            CurrentSessionEndUtc = currentSession.SessionEndUtc;
            QueryFromUtc = queryFromUtc;
            WindowStartUtc = previousSession.SessionStartUtc;
            WindowEndUtc = currentSession.SessionEndUtc;
        }

        public DateTime QueryFromUtc { get; }
        public DateTime WindowStartUtc { get; }
        public DateTime WindowEndUtc { get; }
        public DateTime PreviousSessionStartUtc { get; }
        public DateTime PreviousSessionEndUtc { get; }
        public DateTime CurrentSessionStartUtc { get; }
        public DateTime CurrentSessionEndUtc { get; }

        public bool IsInCurrentSession(DateTime utcTimestamp)
            => IsInSessionDate(utcTimestamp, _currentSessionDate);

        public bool IsInPreviousSession(DateTime utcTimestamp)
            => IsInSessionDate(utcTimestamp, _previousSessionDate);

        public bool IsInPreviousOrCurrentSession(DateTime utcTimestamp)
            => IsInSessionDate(utcTimestamp, _currentSessionDate) || IsInSessionDate(utcTimestamp, _previousSessionDate);

        public bool IsInPreviousOrCurrentSession(DateTime utcTimestamp, DateOnly fallbackPreviousSessionDate)
            => IsInSessionDate(utcTimestamp, _currentSessionDate) || IsInSessionDate(utcTimestamp, fallbackPreviousSessionDate);

        public bool CurrentSessionHasCandles(IReadOnlyCollection<StockHistoricalPrice> rows)
            => rows.Any(x => IsInCurrentSession(x.Timestamp));

        public DateOnly? TryFindLatestPreviousSessionDateWithData(IReadOnlyCollection<StockHistoricalPrice> rows)
        {
            DateOnly? best = null;
            foreach (var row in rows)
            {
                var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(row.Timestamp, _timeZone);
                if (!TradingSessionCalendar.IsTradingDay(localDate, _spec))
                {
                    continue;
                }

                if (localDate >= _currentSessionDate)
                {
                    continue;
                }

                if (best is null || localDate > best.Value)
                {
                    best = localDate;
                }
            }

            return best;
        }

        private bool IsInSessionDate(DateTime utcTimestamp, DateOnly sessionDateLocal)
        {
            var localDate = TradingSessionCalendar.ConvertUtcToLocalDate(utcTimestamp, _timeZone);
            return localDate == sessionDateLocal && TradingSessionCalendar.IsTradingDay(localDate, _spec);
        }
    }

    private sealed record CandleData(
        DateTime Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal? AdjustedClose,
        long Volume);

    private sealed record CandleBatch(
        IReadOnlyList<CandleData> Candles,
        string? QuoteCurrency,
        string? FinancialCurrency,
        string? NormalizedQuoteCurrency,
        decimal QuoteUnitMultiplier)
    {
        public static CandleBatch Empty { get; } = new(Array.Empty<CandleData>(), null, null, null, 1m);
    }

    private sealed record CandleFetchResult(CandleBatch Batch, bool WasRateLimited)
    {
        public static CandleFetchResult Success(CandleBatch batch) => new(batch, false);
        public static CandleFetchResult RateLimited() => new(CandleBatch.Empty, true);

        public static implicit operator CandleBatch(CandleFetchResult result) => result.Batch;
    }

    private sealed record HistoryBatchFetchResult(IReadOnlyList<IntervalBatch> Batches, bool WasRateLimited)
    {
        public static HistoryBatchFetchResult Success(IReadOnlyList<IntervalBatch> batches) => new(batches, false);
        public static HistoryBatchFetchResult RateLimited() => new(Array.Empty<IntervalBatch>(), true);
    }

    private sealed record LatestMetricsContext(
        StockHistoricalPrice? Point,
        int Index,
        bool UsesCompletedCandle);
}
