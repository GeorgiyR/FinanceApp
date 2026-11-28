using System.Collections.Concurrent;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

public interface IFundamentalsService
{
    Task<FundamentalsResult> GetFundamentalsAsync(int stockId, CancellationToken ct = default);
    Task<FundamentalsResult> RefreshFundamentalsAsync(int stockId, CancellationToken ct = default);
}

public enum FundamentalsRefreshFailureCategory
{
    None = 0,
    ProviderRateLimited,
    ProviderFailure
}

public sealed record FundamentalsResult(
    CompanyFundamentalsSnapshot? Snapshot,
    FundamentalsState State,
    string? WarningMessage,
    FundamentalsRefreshFailureCategory FailureCategory = FundamentalsRefreshFailureCategory.None)
{
    public static FundamentalsResult FromSnapshot(
        CompanyFundamentalsSnapshot snapshot,
        FundamentalsState state,
        string? warningMessage = null) =>
        new(snapshot, state, warningMessage, FundamentalsRefreshFailureCategory.None);

    public static FundamentalsResult Unavailable(string? warningMessage) =>
        new(null, FundamentalsState.Unavailable, warningMessage, FundamentalsRefreshFailureCategory.ProviderFailure);
}

public sealed class FundamentalsService : IFundamentalsService
{
    private static readonly ConcurrentDictionary<int, RefreshLockEntry> StockRefreshLocks = new();

    private readonly AppDbContext _dbContext;
    private readonly IYahooFundamentalsService _yahooFundamentalsService;
    private readonly YahooFinanceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FundamentalsService> _logger;

    public FundamentalsService(
        AppDbContext dbContext,
        IYahooFundamentalsService yahooFundamentalsService,
        IOptions<YahooFinanceOptions> options,
        TimeProvider timeProvider,
        ILogger<FundamentalsService> logger)
    {
        _dbContext = dbContext;
        _yahooFundamentalsService = yahooFundamentalsService;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<FundamentalsResult> GetFundamentalsAsync(int stockId, CancellationToken ct = default)
    {
        _ = await GetStockOrThrowAsync(stockId, ct);
        var existing = await LoadSnapshotAsync(stockId, track: false, ct);
        if (existing is not null && IsFresh(existing))
        {
            return FundamentalsResult.FromSnapshot(existing, FundamentalsState.Fresh);
        }

        return await RefreshFundamentalsAsync(stockId, ct);
    }

    public async Task<FundamentalsResult> RefreshFundamentalsAsync(int stockId, CancellationToken ct = default)
    {
        var stock = await GetStockOrThrowAsync(stockId, ct);
        var lockEntry = RentRefreshLock(stockId);
        await lockEntry.Semaphore.WaitAsync(ct);
        try
        {
            var trackedSnapshot = await LoadSnapshotAsync(stockId, track: true, ct);
            var canonicalStock = await ResolveCanonicalStockAsync(stock, ct);
            var sourceSymbol = StockExchanges.ResolveProviderSymbol(canonicalStock.Ticker, canonicalStock.Exchange);
            var providerResult = await _yahooFundamentalsService.GetFundamentalsAsync(sourceSymbol, ct);

            if (!providerResult.IsSuccess || providerResult.Snapshot is null)
            {
                _logger.LogWarning(
                    "Yahoo fundamentals refresh failed for stock {StockId} via symbol {SourceSymbol}: status={StatusCode}",
                    stockId,
                    sourceSymbol,
                    providerResult.StatusCode);

                if (trackedSnapshot is not null)
                {
                    return FundamentalsResult.FromSnapshot(
                        trackedSnapshot,
                        FundamentalsState.Stale,
                        "Не удалось обновить фундаментальные данные. Показан сохранённый снимок.")
                    with
                    {
                        FailureCategory = providerResult.FailureCategory == YahooFundamentalsFailureCategory.ProviderRateLimited
                            ? FundamentalsRefreshFailureCategory.ProviderRateLimited
                            : FundamentalsRefreshFailureCategory.ProviderFailure
                    };
                }

                return FundamentalsResult.Unavailable("Не удалось загрузить фундаментальные данные.")
                    with
                    {
                        FailureCategory = providerResult.FailureCategory == YahooFundamentalsFailureCategory.ProviderRateLimited
                            ? FundamentalsRefreshFailureCategory.ProviderRateLimited
                            : FundamentalsRefreshFailureCategory.ProviderFailure
                    };
            }

            var persisted = await UpsertSnapshotAsync(stockId, providerResult.Snapshot, trackedSnapshot, ct);
            return FundamentalsResult.FromSnapshot(persisted, FundamentalsState.Fresh);
        }
        finally
        {
            lockEntry.Semaphore.Release();
            ReturnRefreshLock(stockId, lockEntry);
        }
    }

    private static RefreshLockEntry RentRefreshLock(int stockId)
    {
        while (true)
        {
            var entry = StockRefreshLocks.GetOrAdd(stockId, static _ => new RefreshLockEntry());
            entry.AddRef();

            if (StockRefreshLocks.TryGetValue(stockId, out var current) && ReferenceEquals(current, entry))
            {
                return entry;
            }

            ReturnRefreshLock(stockId, entry);
        }
    }

    private static void ReturnRefreshLock(int stockId, RefreshLockEntry entry)
    {
        if (entry.ReleaseRef() == 0)
        {
            StockRefreshLocks.TryRemove(new KeyValuePair<int, RefreshLockEntry>(stockId, entry));
        }
    }

    private async Task<Stock> GetStockOrThrowAsync(int stockId, CancellationToken ct)
    {
        var stock = await _dbContext.Stocks.FirstOrDefaultAsync(x => x.Id == stockId, ct);
        return stock ?? throw new KeyNotFoundException($"Stock {stockId} was not found.");
    }

    private async Task<CompanyFundamentalsSnapshot?> LoadSnapshotAsync(int stockId, bool track, CancellationToken ct)
    {
        var query = _dbContext.FundamentalsSnapshots
            .Include(x => x.Periods)
            .Include(x => x.EarningsEvents)
            .Where(x => x.StockId == stockId);

        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    private bool IsFresh(CompanyFundamentalsSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var fundamentalsFresh = now - snapshot.FetchedAtUtc <= _options.FundamentalsCacheDuration;
        if (!fundamentalsFresh)
        {
            return false;
        }

        var earningsFetchedAt = snapshot.EarningsEvents.Count == 0
            ? snapshot.FetchedAtUtc
            : snapshot.EarningsEvents.Max(x => x.FetchedAtUtc);

        return now - earningsFetchedAt <= _options.EarningsCacheDuration;
    }

    private async Task<Stock> ResolveCanonicalStockAsync(Stock stock, CancellationToken ct)
    {
        List<Stock> candidates;
        if (!string.IsNullOrWhiteSpace(stock.Isin))
        {
            var isin = stock.Isin.Trim();
            candidates = await _dbContext.Stocks
                .Where(x => x.Isin == isin)
                .AsNoTracking()
                .ToListAsync(ct);
        }
        else
        {
            var commonName = stock.CommonName.Trim();
            var name = stock.Name.Trim();
            candidates = await _dbContext.Stocks
                .Where(x => x.CommonName == commonName || x.Name == name)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        return candidates
            .DefaultIfEmpty(stock)
            .OrderByDescending(x => string.Equals(x.Exchange, StockExchanges.Nyse, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Id)
            .First();
    }

    private async Task<CompanyFundamentalsSnapshot> UpsertSnapshotAsync(
        int stockId,
        CompanyFundamentalsSnapshot providerSnapshot,
        CompanyFundamentalsSnapshot? existing,
        CancellationToken ct)
    {
        providerSnapshot.StockId = stockId;

        if (existing is null)
        {
            _dbContext.FundamentalsSnapshots.Add(providerSnapshot);
            await _dbContext.SaveChangesAsync(ct);
            return providerSnapshot;
        }

        existing.SourceSymbol = providerSnapshot.SourceSymbol;
        existing.MarketCap = providerSnapshot.MarketCap;
        existing.EnterpriseValue = providerSnapshot.EnterpriseValue;
        existing.TotalDebt = providerSnapshot.TotalDebt;
        existing.CashAndEquivalents = providerSnapshot.CashAndEquivalents;
        existing.RevenueTtm = providerSnapshot.RevenueTtm;
        existing.NetIncomeTtm = providerSnapshot.NetIncomeTtm;
        existing.EbitdaTtm = providerSnapshot.EbitdaTtm;
        existing.OperatingIncomeTtm = providerSnapshot.OperatingIncomeTtm;
        existing.FreeCashFlowTtm = providerSnapshot.FreeCashFlowTtm;
        existing.TotalAssets = providerSnapshot.TotalAssets;
        existing.TotalLiabilities = providerSnapshot.TotalLiabilities;
        existing.PeRatio = providerSnapshot.PeRatio;
        existing.ForwardPeRatio = providerSnapshot.ForwardPeRatio;
        existing.PbRatio = providerSnapshot.PbRatio;
        existing.DividendYield = providerSnapshot.DividendYield;
        existing.Currency = providerSnapshot.Currency;
        existing.Source = providerSnapshot.Source;
        existing.AsOfDate = providerSnapshot.AsOfDate;
        existing.FetchedAtUtc = providerSnapshot.FetchedAtUtc;

        var oldEarningsEvents = existing.EarningsEvents
            .Select(x => new EarningsDateHint(
                x.FiscalPeriodEnd,
                x.FiscalPeriod,
                x.ReportDate,
                x.ReportDateEnd,
                x.DateStatus,
                x.EpsEstimate,
                x.EpsReported,
                x.RevenueEstimate,
                x.RevenueReported,
                x.Source,
                x.FetchedAtUtc))
            .ToList();

        _dbContext.FinancialPeriods.RemoveRange(existing.Periods);
        _dbContext.EarningsEvents.RemoveRange(existing.EarningsEvents);
        existing.Periods.Clear();
        existing.EarningsEvents.Clear();

        foreach (var period in providerSnapshot.Periods)
        {
            existing.Periods.Add(period);
        }

        foreach (var earningsEvent in providerSnapshot.EarningsEvents)
        {
            if (earningsEvent.ReportDate is null && earningsEvent.FiscalPeriodEnd is DateTime periodEnd)
            {
                var hint = oldEarningsEvents.FirstOrDefault(x =>
                    x.FiscalPeriodEnd == periodEnd && x.FiscalPeriod == earningsEvent.FiscalPeriod);
                if (hint is not null)
                {
                    earningsEvent.ReportDate = hint.ReportDate;
                    earningsEvent.ReportDateEnd ??= hint.ReportDateEnd;
                }
            }

            existing.EarningsEvents.Add(earningsEvent);
        }

        // Yahoo drops past report dates from calendarEvents once a report is published.
        // Keep calendar-only events whose actual date was learned in an earlier snapshot.
        var knownDates = existing.EarningsEvents
            .Where(x => x.ReportDate.HasValue)
            .Select(x => x.ReportDate!.Value)
            .ToHashSet();
        var knownPeriodEnds = existing.EarningsEvents
            .Where(x => x.FiscalPeriodEnd.HasValue)
            .Select(x => x.FiscalPeriodEnd!.Value)
            .ToHashSet();

        foreach (var hint in oldEarningsEvents.Where(x => x.ReportDate.HasValue))
        {
            var date = hint.ReportDate!.Value;
            if (knownDates.Contains(date) || knownPeriodEnds.Contains(date)) continue;

            existing.EarningsEvents.Add(new EarningsEvent
            {
                FiscalPeriodEnd = hint.FiscalPeriodEnd,
                FiscalPeriod = hint.FiscalPeriod,
                ReportDate = hint.ReportDate,
                ReportDateEnd = hint.ReportDateEnd,
                DateStatus = hint.DateStatus,
                EpsEstimate = hint.EpsEstimate,
                EpsReported = hint.EpsReported,
                RevenueEstimate = hint.RevenueEstimate,
                RevenueReported = hint.RevenueReported,
                Source = hint.Source,
                FetchedAtUtc = hint.FetchedAtUtc,
            });
            knownDates.Add(date);
        }

        await _dbContext.SaveChangesAsync(ct);
        return existing;
    }

    private sealed record EarningsDateHint(
        DateTime? FiscalPeriodEnd,
        string? FiscalPeriod,
        DateTime? ReportDate,
        DateTime? ReportDateEnd,
        EarningsDateStatus DateStatus,
        decimal? EpsEstimate,
        decimal? EpsReported,
        decimal? RevenueEstimate,
        decimal? RevenueReported,
        string Source,
        DateTime FetchedAtUtc);

    private sealed class RefreshLockEntry
    {
        private int _refCount;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public void AddRef() => Interlocked.Increment(ref _refCount);

        public int ReleaseRef() => Interlocked.Decrement(ref _refCount);
    }
}
