using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceApp.API.Models;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Services;

public class MarketIndexHistoryService : IMarketIndexHistoryService
{
    private const int MaxYahooRequestAttempts = 5;
    private static readonly TimeSpan YahooRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan YahooRetryMaxDelay = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> IndexRefreshLocks = new();

    // Provider symbol allowlist: letters, digits, ^, ., -, _ only; max 50 chars
    private static readonly Regex ProviderSymbolRegex = new(@"^[A-Za-z0-9\^\.\-_]{1,50}$", RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;
    private readonly IYahooRequestCoordinator _yahooRequestCoordinator;
    private readonly IYahooQuoteService _yahooQuoteService;
    private readonly ILogger<MarketIndexHistoryService> _logger;

    public MarketIndexHistoryService(
        AppDbContext dbContext,
        IYahooRequestCoordinator yahooRequestCoordinator,
        IYahooQuoteService yahooQuoteService,
        ILogger<MarketIndexHistoryService> logger)
    {
        _dbContext = dbContext;
        _yahooRequestCoordinator = yahooRequestCoordinator;
        _yahooQuoteService = yahooQuoteService;
        _logger = logger;
    }

    public async Task<MarketIndexHistoryResponse> GetHistoryAsync(
        MarketIndex index,
        string range,
        CancellationToken cancellationToken = default)
    {
        var normalizedRange = NormalizeRange(range);
        var interval = GetInterval(normalizedRange);
        var from = GetFromTimestamp(normalizedRange);

        var data = await LoadHistoryRowsAsync(index.Id, interval, from, cancellationToken);

        bool isStale = false;
        string? staleReason = null;
        decimal? currentPrice = null;
        DateTime? currentPriceAt = null;
        bool currentPriceIsDelayed = false;
        string? currentPriceDelayWarning = null;

        if (data.Count == 0 && !string.IsNullOrWhiteSpace(index.ProviderSymbol))
        {
            try
            {
                await FetchAndPersistHistoryAsync(index, cancellationToken);
                data = await LoadHistoryRowsAsync(index.Id, interval, from, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "On-demand index history sync failed for MarketIndex {IndexId}", index.Id);
                isStale = true;
                staleReason = "Не удалось загрузить исторические данные от поставщика";
            }
        }

        if (!string.IsNullOrWhiteSpace(index.ProviderSymbol))
        {
            var quoteSnapshot = await TryGetCurrentQuoteSnapshotAsync(index.ProviderSymbol!, cancellationToken);
            if (quoteSnapshot is not null)
            {
                currentPrice = quoteSnapshot.CurrentPrice;
                currentPriceAt = quoteSnapshot.PriceTimestampUtc;
                currentPriceIsDelayed = quoteSnapshot.IsDelayed;
                currentPriceDelayWarning = quoteSnapshot.DelayReason;
            }
        }

        return new MarketIndexHistoryResponse
        {
            MarketIndexId = index.Id,
            Range = normalizedRange,
            Interval = interval,
            CurrentPrice = currentPrice,
            CurrentPriceAt = currentPriceAt,
            CurrentPriceIsDelayed = currentPriceIsDelayed,
            CurrentPriceDelayWarning = currentPriceDelayWarning,
            IsStale = isStale,
            StaleReason = staleReason,
            Points = data.Select(MapPoint).ToList()
        };
    }

    private async Task<YahooQuoteData?> TryGetCurrentQuoteSnapshotAsync(string providerSymbol, CancellationToken cancellationToken)
    {
        try
        {
            var quoteResult = await _yahooQuoteService.GetQuoteAsync(providerSymbol, cancellationToken);
            if (!quoteResult.IsSuccess || quoteResult.Quote is null || quoteResult.Quote.CurrentPrice <= 0)
            {
                return null;
            }

            return quoteResult.Quote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Skipping market-index current quote snapshot for symbol {ProviderSymbol}", providerSymbol);
            return null;
        }
    }

    public async Task<MarketIndexRefreshResponse> RefreshHistoryAsync(
        MarketIndex index,
        string range,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(index.ProviderSymbol))
        {
            throw new InvalidOperationException("Символ поставщика не задан. Укажите ProviderSymbol для загрузки графика.");
        }

        if (!IsValidProviderSymbol(index.ProviderSymbol))
        {
            throw new InvalidOperationException($"Символ поставщика содержит недопустимые символы: {index.ProviderSymbol}");
        }

        var indexLock = IndexRefreshLocks.GetOrAdd(index.Id, static _ => new SemaphoreSlim(1, 1));
        await indexLock.WaitAsync(cancellationToken);
        try
        {
            return await RefreshHistoryCoreAsync(index, cancellationToken);
        }
        finally
        {
            indexLock.Release();
            // Remove idle semaphore to avoid unbounded growth; harmless race is fine.
            if (indexLock.CurrentCount == 1)
            {
                IndexRefreshLocks.TryRemove(new KeyValuePair<int, SemaphoreSlim>(index.Id, indexLock));
            }
        }
    }

    private async Task<MarketIndexRefreshResponse> RefreshHistoryCoreAsync(
        MarketIndex index,
        CancellationToken cancellationToken)
    {
        var providerSymbol = index.ProviderSymbol!;
        var fetchedAt = DateTime.UtcNow;

        var monthly = await FetchCandlesAsync(providerSymbol, "1mo", "5y", cancellationToken);
        if (monthly.WasRateLimited)
        {
            _logger.LogInformation("Yahoo rate limited for MarketIndex {IndexId}; skipping refresh", index.Id);
            return new MarketIndexRefreshResponse { MarketIndexId = index.Id };
        }

        var weekly = await FetchCandlesAsync(providerSymbol, "1wk", "1y", cancellationToken);
        if (weekly.WasRateLimited)
        {
            return new MarketIndexRefreshResponse { MarketIndexId = index.Id };
        }

        var daily = await FetchCandlesAsync(providerSymbol, "1d", "1y", cancellationToken);
        if (daily.WasRateLimited)
        {
            return new MarketIndexRefreshResponse { MarketIndexId = index.Id };
        }

        var hourly = await FetchCandlesAsync(providerSymbol, "1h", "7d", cancellationToken);
        if (hourly.WasRateLimited)
        {
            return new MarketIndexRefreshResponse { MarketIndexId = index.Id };
        }

        var fiveMinute = await FetchCandlesAsync(providerSymbol, "5m", "1d", cancellationToken);
        if (fiveMinute.WasRateLimited)
        {
            return new MarketIndexRefreshResponse { MarketIndexId = index.Id };
        }

        var tenMinute = AggregateToTenMinute(fiveMinute.Batch);

        var batches = new[]
        {
            ("1mo", monthly.Batch),
            ("1wk", weekly.Batch),
            ("1d", daily.Batch),
            ("1h", hourly.Batch),
            ("10m", tenMinute),
        };

        var replacementRows = BuildReplacementRows(index.Id, providerSymbol, fetchedAt, batches);
        var importedPoints = replacementRows.Count;
        var deletedPoints = await ReplaceHistoryAsync(index.Id, replacementRows, cancellationToken);

        return new MarketIndexRefreshResponse
        {
            MarketIndexId = index.Id,
            DeletedPoints = deletedPoints,
            ImportedPoints = importedPoints
        };
    }

    private async Task FetchAndPersistHistoryAsync(MarketIndex index, CancellationToken cancellationToken)
    {
        var providerSymbol = index.ProviderSymbol!;
        if (!IsValidProviderSymbol(providerSymbol))
        {
            return;
        }

        var indexLock = IndexRefreshLocks.GetOrAdd(index.Id, static _ => new SemaphoreSlim(1, 1));
        if (!await indexLock.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            await RefreshHistoryCoreAsync(index, cancellationToken);
        }
        finally
        {
            indexLock.Release();
        }
    }

    private async Task<CandleFetchResult> FetchCandlesAsync(
        string symbol,
        string interval,
        string range,
        CancellationToken cancellationToken)
    {
        var encodedSymbol = Uri.EscapeDataString(symbol);
        var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{encodedSymbol}?interval={interval}&range={range}";
        var requestLabel = $"index-history:{interval}:{range}";

        try
        {
            var response = await _yahooRequestCoordinator.GetAsync(
                url,
                requestLabel,
                new YahooRequestExecutionOptions(MaxYahooRequestAttempts, YahooRetryBaseDelay, YahooRetryMaxDelay),
                cancellationToken);

            if (response.IsRateLimited)
            {
                _logger.LogWarning(
                    "Yahoo index history rate limited for interval={Interval} range={Range}; cooldownUntilUtc={CooldownUntilUtc}",
                    interval, range, response.CooldownUntilUtc);
                return CandleFetchResult.RateLimited();
            }

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning(
                    "Yahoo index history request failed for interval={Interval} range={Range}: {StatusCode}",
                    interval, range, (int)response.StatusCode);
                return CandleFetchResult.Success(IndexCandleBatch.Empty);
            }

            using var doc = JsonDocument.Parse(response.Content);
            return CandleFetchResult.Success(ParseCandles(doc.RootElement));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Yahoo index history request timed out for interval={Interval} range={Range}", interval, range);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Yahoo index history request failed for interval={Interval} range={Range}", interval, range);
            throw;
        }
    }

    private static IndexCandleBatch ParseCandles(JsonElement root)
    {
        if (!root.TryGetProperty("chart", out var chart) ||
            !chart.TryGetProperty("result", out var resultArray) ||
            resultArray.GetArrayLength() == 0)
        {
            return IndexCandleBatch.Empty;
        }

        var result = resultArray[0];
        if (!result.TryGetProperty("timestamp", out var timestamps) ||
            !result.TryGetProperty("indicators", out var indicators) ||
            !indicators.TryGetProperty("quote", out var quoteArray) ||
            quoteArray.GetArrayLength() == 0)
        {
            return IndexCandleBatch.Empty;
        }

        var quote = quoteArray[0];
        if (!quote.TryGetProperty("close", out var closeArray))
        {
            return IndexCandleBatch.Empty;
        }

        var openArray = quote.TryGetProperty("open", out var openEl) ? openEl : default;
        var highArray = quote.TryGetProperty("high", out var highEl) ? highEl : default;
        var lowArray = quote.TryGetProperty("low", out var lowEl) ? lowEl : default;
        var volumeArray = quote.TryGetProperty("volume", out var volumeEl) ? volumeEl : default;

        var candles = new List<IndexCandleData>();
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

            if (close <= 0m)
            {
                continue;
            }

            var open = TryGetDecimal(openArray, i, out var parsedOpen) && parsedOpen > 0m ? parsedOpen : close;
            var high = TryGetDecimal(highArray, i, out var parsedHigh) && parsedHigh > 0m ? parsedHigh : close;
            var low = TryGetDecimal(lowArray, i, out var parsedLow) && parsedLow > 0m ? parsedLow : close;
            long? volume = TryGetInt64(volumeArray, i, out var parsedVolume) && parsedVolume > 0L ? parsedVolume : null;

            candles.Add(new IndexCandleData(
                DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime,
                open, high, low, close, volume));
        }

        return new IndexCandleBatch(candles.OrderBy(x => x.Timestamp).ToList());
    }

    private static IndexCandleBatch AggregateToTenMinute(IndexCandleBatch fiveMinuteCandles)
    {
        var aggregated = fiveMinuteCandles.Candles
            .GroupBy(x => new DateTime(
                x.Timestamp.Year, x.Timestamp.Month, x.Timestamp.Day,
                x.Timestamp.Hour, (x.Timestamp.Minute / 10) * 10, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.Timestamp).ToList();
                long? volumeSum = ordered.Any(x => x.Volume.HasValue)
                    ? ordered.Sum(x => x.Volume ?? 0L)
                    : null;
                return new IndexCandleData(
                    g.Key,
                    ordered.First().Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    ordered.Last().Close,
                    volumeSum > 0 ? volumeSum : null);
            })
            .ToList();

        return new IndexCandleBatch(aggregated);
    }

    private static List<MarketIndexHistoricalPrice> BuildReplacementRows(
        int indexId,
        string providerSymbol,
        DateTime fetchedAt,
        IEnumerable<(string Interval, IndexCandleBatch Batch)> batches)
    {
        var rows = new List<MarketIndexHistoricalPrice>();
        foreach (var (interval, batch) in batches)
        {
            foreach (var candle in batch.Candles)
            {
                rows.Add(new MarketIndexHistoricalPrice
                {
                    MarketIndexId = indexId,
                    Timestamp = candle.Timestamp,
                    Interval = interval,
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    Volume = candle.Volume,
                    Provider = "Yahoo Finance",
                    ProviderSymbol = providerSymbol,
                    FetchedAt = fetchedAt
                });
            }
        }

        return rows;
    }

    private async Task<int> ReplaceHistoryAsync(
        int indexId,
        IReadOnlyCollection<MarketIndexHistoricalPrice> replacementRows,
        CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            return await ReplaceHistoryWithoutTransactionAsync(indexId, replacementRows, cancellationToken);
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
        var deletedPoints = 0;

        await executionStrategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var existing = await _dbContext.MarketIndexHistoricalPrices
                .Where(x => x.MarketIndexId == indexId)
                .ToListAsync(cancellationToken);
            deletedPoints = existing.Count;

            if (deletedPoints > 0)
            {
                _dbContext.MarketIndexHistoricalPrices.RemoveRange(existing);
            }

            _dbContext.MarketIndexHistoricalPrices.AddRange(CloneRows(replacementRows));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        _dbContext.ChangeTracker.Clear();
        return deletedPoints;
    }

    private async Task<int> ReplaceHistoryWithoutTransactionAsync(
        int indexId,
        IReadOnlyCollection<MarketIndexHistoricalPrice> replacementRows,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();

        var existing = await _dbContext.MarketIndexHistoricalPrices
            .Where(x => x.MarketIndexId == indexId)
            .ToListAsync(cancellationToken);
        var deletedPoints = existing.Count;

        if (deletedPoints > 0)
        {
            _dbContext.MarketIndexHistoricalPrices.RemoveRange(existing);
        }

        _dbContext.MarketIndexHistoricalPrices.AddRange(CloneRows(replacementRows));
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
        return deletedPoints;
    }

    private static IEnumerable<MarketIndexHistoricalPrice> CloneRows(IEnumerable<MarketIndexHistoricalPrice> rows)
    {
        foreach (var row in rows)
        {
            yield return new MarketIndexHistoricalPrice
            {
                MarketIndexId = row.MarketIndexId,
                Timestamp = row.Timestamp,
                Interval = row.Interval,
                Open = row.Open,
                High = row.High,
                Low = row.Low,
                Close = row.Close,
                Volume = row.Volume,
                Provider = row.Provider,
                ProviderSymbol = row.ProviderSymbol,
                FetchedAt = row.FetchedAt
            };
        }
    }

    private async Task<List<MarketIndexHistoricalPrice>> LoadHistoryRowsAsync(
        int indexId, string interval, DateTime from, CancellationToken cancellationToken)
    {
        return await _dbContext.MarketIndexHistoricalPrices
            .AsNoTracking()
            .Where(x => x.MarketIndexId == indexId && x.Interval == interval && x.Timestamp >= from)
            .OrderBy(x => x.Timestamp)
            .ToListAsync(cancellationToken);
    }

    private static MarketIndexHistoryPointDto MapPoint(MarketIndexHistoricalPrice row) => new()
    {
        Timestamp = row.Timestamp,
        Interval = row.Interval,
        Open = row.Open,
        High = row.High,
        Low = row.Low,
        Close = row.Close,
        Volume = row.Volume
    };

    public static bool IsValidProviderSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        return ProviderSymbolRegex.IsMatch(symbol);
    }

    public static string NormalizeRange(string range)
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
            _ => throw new ArgumentException($"Неподдерживаемый диапазон: '{range}'. Допустимые значения: 5y, 3y, 1y, 6m, 3m, 1m, 1w, 24h, today.")
        };
    }

    public static string GetInterval(string normalizedRange) => normalizedRange switch
    {
        "5y" or "3y" => "1mo",
        "1y" => "1wk",
        "6m" or "3m" or "1m" => "1d",
        "1w" => "1h",
        "24h" or "today" => "10m",
        _ => "1mo"
    };

    private static DateTime GetFromTimestamp(string normalizedRange)
    {
        var now = DateTime.UtcNow;
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

    private static bool TryGetDecimal(JsonElement arrayElement, int index, out decimal value)
    {
        value = 0m;
        if (arrayElement.ValueKind != JsonValueKind.Array || index < 0 || index >= arrayElement.GetArrayLength())
            return false;

        var element = arrayElement[index];
        if (element.ValueKind == JsonValueKind.Null)
            return false;

        try
        {
            if (element.TryGetDecimal(out value))
                return true;

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
            return false;

        var element = arrayElement[index];
        if (element.ValueKind == JsonValueKind.Null)
            return false;

        try
        {
            if (element.TryGetInt64(out value))
                return true;

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

    private sealed record IndexCandleData(
        DateTime Timestamp,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long? Volume);

    private sealed record IndexCandleBatch(IReadOnlyList<IndexCandleData> Candles)
    {
        public static IndexCandleBatch Empty { get; } = new(Array.Empty<IndexCandleData>());
    }

    private sealed record CandleFetchResult(IndexCandleBatch Batch, bool WasRateLimited)
    {
        public static CandleFetchResult Success(IndexCandleBatch batch) => new(batch, false);
        public static CandleFetchResult RateLimited() => new(IndexCandleBatch.Empty, true);
    }
}
