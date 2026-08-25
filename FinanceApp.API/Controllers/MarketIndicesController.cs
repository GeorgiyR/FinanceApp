using System.Collections.Concurrent;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/market-indices")]
[Authorize]
public class MarketIndicesController : ControllerBase
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> RefreshLocks = new();
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> BatchHistoryRefreshLocks = new();
    private const int MaxBatchResultDetails = 200;

    private readonly AppDbContext _context;
    private readonly IMarketIndexHistoryService _historyService;
    private readonly IIndexConstituentsProvider _constituentsProvider;
    private readonly IStockHistoryService _stockHistoryService;
    private readonly IStockPerformanceCalculationService _stockPerformanceCalculationService;
    private readonly IIndexConstituentHistoryRefreshJobService _constituentHistoryRefreshJobs;
    private readonly IIndexConstituentsBatchQuoteRefreshJobService _constituentsBatchQuoteRefreshJobs;
    private readonly IStockMetadataEnrichmentService? _stockMetadataEnrichmentService;
    private readonly ILogger<MarketIndicesController> _logger;

    public MarketIndicesController(
        AppDbContext context,
        IMarketIndexHistoryService historyService,
        IIndexConstituentsProvider constituentsProvider,
        IStockHistoryService stockHistoryService,
        IStockPerformanceCalculationService stockPerformanceCalculationService,
        IIndexConstituentHistoryRefreshJobService constituentHistoryRefreshJobs,
        IIndexConstituentsBatchQuoteRefreshJobService constituentsBatchQuoteRefreshJobs,
        ILogger<MarketIndicesController> logger,
        IStockMetadataEnrichmentService? stockMetadataEnrichmentService = null)
    {
        _context = context;
        _historyService = historyService;
        _constituentsProvider = constituentsProvider;
        _stockHistoryService = stockHistoryService;
        _stockPerformanceCalculationService = stockPerformanceCalculationService;
        _constituentHistoryRefreshJobs = constituentHistoryRefreshJobs;
        _constituentsBatchQuoteRefreshJobs = constituentsBatchQuoteRefreshJobs;
        _stockMetadataEnrichmentService = stockMetadataEnrichmentService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<MarketIndexDto>>> GetAll([FromQuery] bool includeArchived = false)
    {
        var marketIndices = await _context.MarketIndices
            .AsNoTracking()
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync();

        return Ok(marketIndices.Select(MapMarketIndex));
    }

    [HttpPost]
    public async Task<ActionResult<MarketIndexDto>> CreateMarketIndex(UpsertMarketIndexRequest request)
    {
        var normalizedName = Normalize(request.Name);
        if (string.IsNullOrEmpty(normalizedName))
        {
            return BadRequest("Название индекса обязательно.");
        }

        var normalizedCode = Normalize(request.Code);
        if (string.IsNullOrEmpty(normalizedCode))
        {
            return BadRequest("Код индекса обязателен.");
        }

        if (await _context.MarketIndices.AnyAsync(x => x.NormalizedCode == normalizedCode))
        {
            return Conflict("Индекс с таким кодом уже существует.");
        }

        var now = DateTime.UtcNow;
        var normalizedProviderSymbol = NormalizeProviderSymbol(request.ProviderSymbol);
        if (normalizedProviderSymbol is not null && !MarketIndexHistoryService.IsValidProviderSymbol(normalizedProviderSymbol))
        {
            return BadRequest("Символ поставщика содержит недопустимые символы.");
        }

        var marketIndex = new MarketIndex
        {
            Name = request.Name.Trim(),
            NormalizedName = normalizedName,
            Code = request.Code.Trim(),
            NormalizedCode = normalizedCode,
            ProviderSymbol = normalizedProviderSymbol,
            Description = request.Description?.Trim() ?? string.Empty,
            CountryOrRegion = request.CountryOrRegion?.Trim() ?? string.Empty,
            SortOrder = request.SortOrder,
            ShowInNavigation = request.ShowInNavigation ?? true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.MarketIndices.Add(marketIndex);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            return Conflict("Индекс с таким кодом уже существует.");
        }

        return StatusCode(StatusCodes.Status201Created, await LoadMarketIndexAsync(marketIndex.Id));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<MarketIndexDto>> UpdateMarketIndex(int id, UpsertMarketIndexRequest request)
    {
        var marketIndex = await _context.MarketIndices.FirstOrDefaultAsync(x => x.Id == id);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        var normalizedName = Normalize(request.Name);
        if (string.IsNullOrEmpty(normalizedName))
        {
            return BadRequest("Название индекса обязательно.");
        }

        var normalizedCode = Normalize(request.Code);
        if (string.IsNullOrEmpty(normalizedCode))
        {
            return BadRequest("Код индекса обязателен.");
        }

        if (await _context.MarketIndices.AnyAsync(x => x.Id != id && x.NormalizedCode == normalizedCode))
        {
            return Conflict("Индекс с таким кодом уже существует.");
        }

        var newProviderSymbol = NormalizeProviderSymbol(request.ProviderSymbol);
        if (newProviderSymbol is not null && !MarketIndexHistoryService.IsValidProviderSymbol(newProviderSymbol))
        {
            return BadRequest("Символ поставщика содержит недопустимые символы.");
        }

        var symbolChanged = !string.Equals(
            marketIndex.ProviderSymbol, newProviderSymbol, StringComparison.OrdinalIgnoreCase);

        marketIndex.Name = request.Name.Trim();
        marketIndex.NormalizedName = normalizedName;
        marketIndex.Code = request.Code.Trim();
        marketIndex.NormalizedCode = normalizedCode;
        marketIndex.ProviderSymbol = newProviderSymbol;
        marketIndex.Description = request.Description?.Trim() ?? string.Empty;
        marketIndex.CountryOrRegion = request.CountryOrRegion?.Trim() ?? string.Empty;
        marketIndex.SortOrder = request.SortOrder;
        if (request.ShowInNavigation.HasValue)
        {
            marketIndex.ShowInNavigation = request.ShowInNavigation.Value;
        }
        marketIndex.UpdatedAt = DateTime.UtcNow;

        // When ProviderSymbol changes, invalidate old history to prevent mixing data from different symbols
        if (symbolChanged)
        {
            var oldHistory = await _context.MarketIndexHistoricalPrices
                .Where(x => x.MarketIndexId == id)
                .ToListAsync();
            if (oldHistory.Count > 0)
            {
                _context.MarketIndexHistoricalPrices.RemoveRange(oldHistory);
            }
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            return Conflict("Индекс с таким кодом уже существует.");
        }

        return Ok(await LoadMarketIndexAsync(marketIndex.Id));
    }

    [HttpPost("{id:int}/archive")]
    public async Task<ActionResult<MarketIndexDto>> ArchiveMarketIndex(int id)
    {
        var marketIndex = await _context.MarketIndices.FirstOrDefaultAsync(x => x.Id == id);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        marketIndex.IsArchived = true;
        marketIndex.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(await LoadMarketIndexAsync(id));
    }

    [HttpPost("{id:int}/restore")]
    public async Task<ActionResult<MarketIndexDto>> RestoreMarketIndex(int id)
    {
        var marketIndex = await _context.MarketIndices.FirstOrDefaultAsync(x => x.Id == id);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        marketIndex.IsArchived = false;
        marketIndex.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(await LoadMarketIndexAsync(id));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteMarketIndex(int id)
    {
        var marketIndex = await _context.MarketIndices.FirstOrDefaultAsync(x => x.Id == id);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (await _context.StockMarketIndices.AnyAsync(x => x.MarketIndexId == id))
        {
            return Conflict("Нельзя удалить индекс, к которому привязаны акции.");
        }

        _context.MarketIndices.Remove(marketIndex);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:int}/history")]
    public async Task<ActionResult<MarketIndexHistoryResponse>> GetHistory(
        int id,
        [FromQuery] string range = "1y",
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (string.IsNullOrWhiteSpace(marketIndex.ProviderSymbol))
        {
            return UnprocessableEntity("Символ поставщика не указан для этого индекса. Укажите ProviderSymbol в настройках индекса.");
        }

        string normalizedRange;
        try
        {
            normalizedRange = MarketIndexHistoryService.NormalizeRange(range);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        try
        {
            var result = await _historyService.GetHistoryAsync(marketIndex, normalizedRange, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(502, "Не удалось загрузить исторические данные. Попробуйте позже.");
        }
    }

    [HttpPost("{id:int}/history/refresh")]
    public async Task<ActionResult<MarketIndexRefreshResponse>> RefreshHistory(
        int id,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (string.IsNullOrWhiteSpace(marketIndex.ProviderSymbol))
        {
            return UnprocessableEntity("Символ поставщика не указан. Укажите ProviderSymbol для обновления истории.");
        }

        if (marketIndex.IsArchived)
        {
            return Conflict("Нельзя обновлять историю архивного индекса через этот endpoint. Снимите индекс с архива.");
        }

        try
        {
            var result = await _historyService.RefreshHistoryAsync(marketIndex, "5y", cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(502, "Не удалось обновить исторические данные. Попробуйте позже.");
        }
    }

    [HttpGet("{id:int}/constituents")]
    public async Task<ActionResult<IndexConstituentsResponse>> GetConstituents(
        int id,
        [FromQuery] bool includeFormer = false,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (marketIndex is null) return NotFound("Индекс не найден.");

        var membershipsQuery = _context.StockMarketIndices
            .Include(x => x.Stock)
            .ThenInclude(x => x.Sector)
            .Include(x => x.Stock)
            .ThenInclude(x => x.Industry)
            .ThenInclude(x => x!.Sector)
            .Where(x => x.MarketIndexId == id);

        if (!includeFormer)
        {
            membershipsQuery = membershipsQuery.Where(x => x.EffectiveTo == null);
        }

        var memberships = await membershipsQuery
            .AsNoTracking()
            .OrderBy(x => x.Stock.Name)
            .ToListAsync(cancellationToken);

        var dtos = memberships.Select(x => new IndexConstituentDto
        {
            StockId = x.StockId,
            Ticker = x.Stock.Ticker,
            ProviderSymbol = x.Stock.ProviderSymbol,
            Name = x.Stock.Name,
            CommonName = x.Stock.CommonName,
            SectorId = x.Stock.Industry?.SectorId ?? x.Stock.SectorId,
            Sector = x.Stock.Industry?.Sector?.Name ?? x.Stock.Sector?.Name,
            IndustryId = x.Stock.IndustryId,
            Industry = x.Stock.Industry?.Name,
            Exchange = x.Stock.Exchange,
            Isin = x.Stock.Isin,
            Wkn = x.Stock.Wkn,
            FinanzenNetSlug = x.Stock.FinanzenNetSlug,
            CurrentPrice = x.Stock.CurrentPrice,
            CurrentPriceChange = x.Stock.CurrentPriceChange,
            CurrentPriceChangePercent = x.Stock.CurrentPriceChangePercent,
            CurrentPriceAt = x.Stock.CurrentPriceAt,
            CurrentPriceIsDelayed = x.Stock.CurrentPriceIsDelayed,
            CurrentPriceDelayWarning = x.Stock.CurrentPriceDelayWarning,
            TrackingStatus = x.Stock.TrackingStatus.ToString(),
            Source = x.Source,
            ProviderConstituentKey = x.ProviderConstituentKey,
            EffectiveFrom = x.EffectiveFrom,
            EffectiveTo = x.EffectiveTo,
            LastVerifiedAt = x.LastVerifiedAt,
            ImportedAt = x.ImportedAt,
        }).ToList();

        var latestMembership = memberships
            .OrderByDescending(x => x.LastVerifiedAt ?? x.ImportedAt)
            .ThenByDescending(x => x.ImportedAt)
            .FirstOrDefault();

        return Ok(new IndexConstituentsResponse
        {
            MarketIndexId = id,
            IndexName = marketIndex.Name,
            TotalCount = dtos.Count,
            Source = latestMembership?.Source,
            AsOfDate = latestMembership?.LastVerifiedAt,
            IsCuratedSnapshot = GetNonEmptyTrimmed(latestMembership?.Source)?
                .Contains("curated snapshot", StringComparison.OrdinalIgnoreCase) == true,
            IsStale = false,
            StaleReason = null,
            Constituents = dtos,
        });
    }

    [HttpGet("{indexId:int}/constituents/{stockId:int}/history")]
    public async Task<ActionResult<StockHistoryResponse>> GetConstituentHistory(
        int indexId,
        int stockId,
        [FromQuery] string range = "5y",
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == indexId, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        var membership = await _context.StockMarketIndices
            .AsNoTracking()
            .Include(x => x.Stock)
            .FirstOrDefaultAsync(
                x => x.MarketIndexId == indexId && x.StockId == stockId && x.EffectiveTo == null,
                cancellationToken);

        if (membership?.Stock is null)
        {
            return NotFound("Акция не входит в текущий состав выбранного индекса.");
        }

        var normalizedRange = (range ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsSupportedStockHistoryRange(normalizedRange))
        {
            return BadRequest("Invalid range. Allowed values: 5y, 3y, 1y, 6m, 3m, 1m, 1w, 24h, today");
        }

        if (!TryValidateTickerAndExchange(membership.Stock, out var validationError))
        {
            return BadRequest(validationError);
        }

        return Ok(await _stockHistoryService.GetHistoryAsync(membership.Stock, normalizedRange, cancellationToken));
    }

    [HttpGet("{indexId:int}/constituents/performance")]
    public async Task<ActionResult<IndexConstituentPerformanceResponse>> GetConstituentPerformance(
        int indexId,
        [FromQuery] string range = "1y",
        CancellationToken cancellationToken = default)
    {
        var normalizedRange = (range ?? string.Empty).Trim().ToLowerInvariant();
        if (!_stockPerformanceCalculationService.IsSupportedRange(normalizedRange))
        {
            return BadRequest("Недопустимый диапазон. Допустимые значения: 5y, 3y, 1y, 6m, 3m, 1m, 1w, 24h, today");
        }

        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == indexId, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        var stocks = await _context.StockMarketIndices
            .AsNoTracking()
            .Where(x => x.MarketIndexId == indexId && x.EffectiveTo == null)
            .Select(x => new StockPerformanceSubject(
                x.StockId,
                x.Stock.Exchange,
                x.Stock.CurrentPrice,
                x.Stock.CurrentPriceChange,
                x.Stock.CurrentPriceChangePercent,
                x.Stock.CurrentPriceAt))
            .ToListAsync(cancellationToken);

        if (stocks.Count == 0)
        {
            return Ok(new IndexConstituentPerformanceResponse
            {
                MarketIndexId = indexId,
                Range = normalizedRange,
                GeneratedAtUtc = DateTime.UtcNow,
                Items = Array.Empty<IndexConstituentPerformanceItemDto>(),
            });
        }

        var items = await _stockPerformanceCalculationService.CalculateAsync(stocks, normalizedRange, cancellationToken);

        return Ok(new IndexConstituentPerformanceResponse
        {
            MarketIndexId = indexId,
            Range = normalizedRange,
            GeneratedAtUtc = DateTime.UtcNow,
            Items = items,
        });
    }

    [HttpPost("{id:int}/constituents/refresh")]
    public async Task<ActionResult<IndexConstituentsRefreshResponse>> RefreshConstituents(
        int id,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (marketIndex is null) return NotFound("Индекс не найден.");

        if (marketIndex.IsArchived)
        {
            return Conflict("Нельзя обновлять состав архивного индекса.");
        }

        var refreshLock = RefreshLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var providerResult = await _constituentsProvider.GetConstituentsAsync(marketIndex, cancellationToken);

            if (providerResult.Status == IndexConstituentsStatus.Unsupported)
            {
                return UnprocessableEntity(CreateRefreshResponse(id, providerResult));
            }

            if (providerResult.Status == IndexConstituentsStatus.RateLimited)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, CreateRefreshResponse(id, providerResult));
            }

            if (providerResult.Status == IndexConstituentsStatus.ProviderFailure)
            {
                return StatusCode(StatusCodes.Status502BadGateway, CreateRefreshResponse(id, providerResult));
            }

            var now = providerResult.FetchedAt;
            int added = 0, updated = 0, unchanged = 0, closed = 0, conflicts = 0;

            var normalizedConstituents = new List<(string Ticker, string ProviderSymbol, string CompanyName, string Exchange, string? Isin, string? Wkn, string? Sector, string? Industry)>();
            var sourceIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var constituent in providerResult.Constituents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ticker = constituent.Ticker?.Trim().ToUpperInvariant();
                var companyName = constituent.CompanyName?.Trim();
                var exchangeRaw = constituent.ProviderExchange?.Trim();
                var providerSymbol = constituent.ProviderSymbol?.Trim();
                var normalizedIsin = StockIdentifiers.Normalize(constituent.Isin);
                var normalizedWkn = StockIdentifiers.Normalize(constituent.Wkn);
                var normalizedSector = NormalizeConstituentClassification(constituent.Sector);
                var normalizedIndustry = NormalizeConstituentClassification(constituent.Industry);

                if (string.IsNullOrWhiteSpace(ticker)
                    || string.IsNullOrWhiteSpace(companyName)
                    || string.IsNullOrWhiteSpace(exchangeRaw)
                    || !StockExchanges.TryNormalize(exchangeRaw, out var normalizedExchange))
                {
                    conflicts++;
                    continue;
                }

                if (normalizedIsin is not null && !StockIdentifiers.IsValidIsin(normalizedIsin))
                {
                    conflicts++;
                    continue;
                }
                if (normalizedWkn is not null && !StockIdentifiers.IsValidWkn(normalizedWkn))
                {
                    conflicts++;
                    continue;
                }

                providerSymbol ??= StockExchanges.ResolveProviderSymbol(ticker, normalizedExchange);
                if (string.IsNullOrWhiteSpace(providerSymbol))
                {
                    conflicts++;
                    continue;
                }

                var sourceIdentity = $"{providerSymbol}|{normalizedExchange}";
                if (!sourceIdentities.Add(sourceIdentity))
                {
                    conflicts++;
                    continue;
                }

                normalizedConstituents.Add((ticker, providerSymbol, companyName, normalizedExchange, normalizedIsin, normalizedWkn, normalizedSector, normalizedIndustry));
            }

            var effectiveStatus = providerResult.Status;
            var canCloseMissingMemberships = providerResult.Status == IndexConstituentsStatus.Success && conflicts == 0;
            if (providerResult.Status == IndexConstituentsStatus.Success && conflicts > 0)
            {
                effectiveStatus = IndexConstituentsStatus.Partial;
            }

            // Load existing current memberships for this index.
            var existingMemberships = await _context.StockMarketIndices
                .Include(x => x.Stock)
                .Where(x => x.MarketIndexId == id && x.EffectiveTo == null)
                .ToListAsync(cancellationToken);

            var allProviderSymbols = normalizedConstituents
                .Select(c => c.ProviderSymbol)
                .ToHashSet(StringComparer.Ordinal);

            var allTickers = normalizedConstituents
                .Select(c => c.Ticker)
                .ToHashSet(StringComparer.Ordinal);

            var sectorKeys = normalizedConstituents
                .Select(c => c.Sector)
                .Where(x => x is not null)
                .Select(x => Normalize(x!))
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var importedSectorKeysByIsin = normalizedConstituents
                .Where(c => c.Isin is not null && c.Sector is not null)
                .GroupBy(c => c.Isin!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(c => Normalize(c.Sector!))
                        .Where(x => x.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.Ordinal);

            var sectorsByNormalizedName = await _context.Sectors
                .Where(x => sectorKeys.Contains(x.NormalizedName))
                .ToDictionaryAsync(x => x.NormalizedName, x => x, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var sectorById = sectorsByNormalizedName.Values
                .Where(x => x.Id > 0)
                .ToDictionary(x => x.Id, x => x);

            var existingStocks = await _context.Stocks
                .Where(s =>
                    (s.ProviderSymbol != null && allProviderSymbols.Contains(s.ProviderSymbol)) ||
                    allTickers.Contains(s.Ticker))
                .ToListAsync(cancellationToken);

            var industrySectorMap = await _context.Industries
                .AsNoTracking()
                .Select(x => new { x.Id, x.SectorId })
                .ToDictionaryAsync(x => x.Id, x => x.SectorId, cancellationToken);

            var byProviderSymbolExchange = existingStocks
                .Where(s => s.ProviderSymbol != null)
                .GroupBy(s => $"{s.ProviderSymbol}|{s.Exchange}")
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var byTickerExchange = existingStocks
                .GroupBy(s => $"{s.Ticker}|{s.Exchange}")
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var seenStocks = new HashSet<Stock>(ReferenceEqualityComparer.Instance);
            var stocksToEnrich = new HashSet<Stock>(ReferenceEqualityComparer.Instance);
            try
            {
                foreach (var constituent in normalizedConstituents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Sector? resolvedSector = null;
                    if (!string.IsNullOrWhiteSpace(constituent.Sector))
                    {
                        var sectorKey = Normalize(constituent.Sector)!;
                        if (!sectorsByNormalizedName.TryGetValue(sectorKey, out resolvedSector))
                        {
                            resolvedSector = new Sector
                            {
                                Name = constituent.Sector!,
                                NormalizedName = sectorKey,
                                SortOrder = 0,
                                IsArchived = false,
                                CreatedAtUtc = now,
                                UpdatedAtUtc = now
                            };
                            _context.Sectors.Add(resolvedSector);
                            sectorsByNormalizedName[sectorKey] = resolvedSector;
                        }
                    }

                    // Listing identity is exchange/provider-symbol/ticker specific.
                    // ISIN is security-level metadata and may legitimately be shared by multiple listings.
                    // Deduplication therefore uses concrete listing identity only.
                    Stock? stock = null;

                    if (stock is null)
                        byProviderSymbolExchange.TryGetValue($"{constituent.ProviderSymbol}|{constituent.Exchange}", out stock);

                    if (stock is null)
                        byTickerExchange.TryGetValue($"{constituent.Ticker}|{constituent.Exchange}", out stock);

                    if (stock is null)
                    {
                        stock = new Stock
                        {
                            Ticker = constituent.Ticker,
                            Name = constituent.CompanyName,
                            CommonName = constituent.CompanyName,
                            Exchange = constituent.Exchange,
                            Isin = constituent.Isin,
                            Wkn = constituent.Wkn,
                            Sector = resolvedSector,
                            ProviderSymbol = constituent.ProviderSymbol,
                            TrackingStatus = StockTrackingStatus.CatalogOnly,
                            UpdatedAt = now,
                        };
                        _context.Stocks.Add(stock);
                        byProviderSymbolExchange[$"{stock.ProviderSymbol}|{stock.Exchange}"] = stock;
                        byTickerExchange[$"{stock.Ticker}|{stock.Exchange}"] = stock;
                        stocksToEnrich.Add(stock);
                    }
                    else
                    {
                        var stockChanged = false;
                        if (!string.Equals(stock.Name, constituent.CompanyName, StringComparison.Ordinal))
                        {
                            var previousName = stock.Name;
                            stock.Name = constituent.CompanyName;
                            if (string.Equals(stock.CommonName, previousName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(stock.CommonName))
                            {
                                stock.CommonName = constituent.CompanyName;
                            }
                            stockChanged = true;
                        }
                        if (stock.Isin is null && constituent.Isin is not null)
                        {
                            stock.Isin = constituent.Isin;
                            stockChanged = true;
                        }
                        if (stock.Wkn is null && constituent.Wkn is not null)
                        {
                            stock.Wkn = constituent.Wkn;
                            stockChanged = true;
                        }
                        if (resolvedSector is not null
                            && stock.SectorId != resolvedSector.Id
                            && CanApplySectorToStock(stock, resolvedSector.Id, industrySectorMap))
                        {
                            stock.Sector = resolvedSector;
                            stockChanged = true;
                        }
                        if (string.IsNullOrWhiteSpace(stock.ProviderSymbol))
                        {
                            stock.ProviderSymbol = constituent.ProviderSymbol;
                            stockChanged = true;
                            stocksToEnrich.Add(stock);
                        }
                        if (stockChanged)
                        {
                            stock.UpdatedAt = now;
                            updated++;
                        }
                    }

                    var membership = existingMemberships.FirstOrDefault(m => ReferenceEquals(m.Stock, stock) || (stock.Id != 0 && m.StockId == stock.Id));
                    if (membership is null)
                    {
                        membership = new StockMarketIndex
                        {
                            Stock = stock,
                            MarketIndexId = id,
                            Source = providerResult.ProviderName,
                            ProviderConstituentKey = constituent.ProviderSymbol,
                            EffectiveFrom = now,
                            ImportedAt = now,
                            LastVerifiedAt = providerResult.AsOfDate ?? now,
                        };
                        _context.StockMarketIndices.Add(membership);
                        existingMemberships.Add(membership);
                        added++;
                    }
                    else
                    {
                        var membershipChanged = false;
                        if (!string.Equals(membership.Source, providerResult.ProviderName, StringComparison.Ordinal))
                        {
                            membership.Source = providerResult.ProviderName;
                            membershipChanged = true;
                        }
                        if (!string.Equals(membership.ProviderConstituentKey, constituent.ProviderSymbol, StringComparison.Ordinal))
                        {
                            membership.ProviderConstituentKey = constituent.ProviderSymbol;
                            membershipChanged = true;
                        }
                        membership.LastVerifiedAt = providerResult.AsOfDate ?? now;
                        if (membershipChanged)
                        {
                            updated++;
                        }
                        else
                        {
                            unchanged++;
                        }
                    }

                    seenStocks.Add(stock);
                }

                if (canCloseMissingMemberships)
                {
                    foreach (var membership in existingMemberships
                        .Where(m => m.EffectiveTo == null && !seenStocks.Contains(m.Stock)))
                    {
                        membership.EffectiveTo = now;
                        closed++;
                    }
                }

                var propagationIsins = importedSectorKeysByIsin.Keys.ToHashSet(StringComparer.Ordinal);
                if (propagationIsins.Count > 0)
                {
                    var stocksWithMatchingIsin = await _context.Stocks
                        .Where(x => x.Isin != null && propagationIsins.Contains(x.Isin))
                        .ToListAsync(cancellationToken);

                    var effectiveSectorIds = stocksWithMatchingIsin
                        .Select(x => GetEffectiveSectorId(x, industrySectorMap))
                        .Where(x => x is not null)
                        .Select(x => x!.Value)
                        .Distinct()
                        .ToArray();

                    if (effectiveSectorIds.Length > 0)
                    {
                        var additionalSectors = await _context.Sectors
                            .Where(x => effectiveSectorIds.Contains(x.Id))
                            .ToListAsync(cancellationToken);

                        foreach (var sector in additionalSectors)
                        {
                            sectorById[sector.Id] = sector;
                        }
                    }

                    foreach (var group in stocksWithMatchingIsin
                                 .Where(x => x.Isin is not null)
                                 .GroupBy(x => x.Isin!, StringComparer.Ordinal))
                    {
                        if (!importedSectorKeysByIsin.TryGetValue(group.Key, out var sourceSectorKeys)
                            || sourceSectorKeys.Count == 0)
                        {
                            continue;
                        }

                        var effectiveSectorKeys = new HashSet<string>(sourceSectorKeys, StringComparer.OrdinalIgnoreCase);
                        foreach (var stock in group)
                        {
                            var effectiveSectorId = GetEffectiveSectorId(stock, industrySectorMap);
                            if (effectiveSectorId is int sectorId
                                && sectorById.TryGetValue(sectorId, out var sector))
                            {
                                effectiveSectorKeys.Add(sector.NormalizedName);
                            }
                        }

                        if (effectiveSectorKeys.Count != 1)
                        {
                            conflicts++;
                            continue;
                        }

                        var propagationSectorKey = effectiveSectorKeys.First();
                        if (!sectorsByNormalizedName.TryGetValue(propagationSectorKey, out var propagationSector))
                        {
                            continue;
                        }

                        foreach (var targetStock in group)
                        {
                            if (targetStock.SectorId is not null)
                            {
                                continue;
                            }

                            if (targetStock.IndustryId is int industryId
                                && industrySectorMap.ContainsKey(industryId))
                            {
                                continue;
                            }

                            targetStock.Sector = propagationSector;
                            targetStock.UpdatedAt = now;
                            updated++;
                        }
                    }
                }

                await _context.SaveChangesAsync(cancellationToken);

                if (_stockMetadataEnrichmentService is not null && stocksToEnrich.Count > 0)
                {
                    try
                    {
                        await _stockMetadataEnrichmentService.EnqueueSelectedAsync(
                            stocksToEnrich.Where(x => x.Id > 0).Select(x => x.Id).Distinct(),
                            "system-index-import",
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to enqueue stock metadata enrichment after index refresh for index {MarketIndexId}.", id);
                    }
                }
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                return Conflict("Конкурентное обновление состава индекса. Повторите попытку.");
            }

            var providerMessage = providerResult.Message;
            if (conflicts > 0)
            {
                var conflictsMessage = $"Пропущено конфликтных/некорректных записей: {conflicts}.";
                providerMessage = string.IsNullOrWhiteSpace(providerMessage)
                    ? conflictsMessage
                    : $"{providerMessage} {conflictsMessage}";
            }

            return Ok(new IndexConstituentsRefreshResponse
            {
                MarketIndexId = id,
                ProviderStatus = effectiveStatus.ToString(),
                ProviderName = providerResult.ProviderName,
                ProviderMessage = providerMessage,
                FetchedAt = providerResult.FetchedAt,
                AsOfDate = providerResult.AsOfDate,
                SourceUrl = providerResult.SourceUrl,
                IsCuratedSnapshot = providerResult.IsCuratedSnapshot,
                IsStale = providerResult.IsStale || effectiveStatus == IndexConstituentsStatus.Partial,
                Added = added,
                Updated = updated,
                Unchanged = unchanged,
                Closed = closed,
                Conflicts = conflicts,
            });
        }
        finally
        {
            refreshLock.Release();
        }
    }

    [HttpPost("{indexId:int}/constituents/{stockId:int}/history/refresh")]
    public async Task<ActionResult<IndexConstituentHistoryRefreshJobResponse>> RefreshConstituentHistory(
        int indexId,
        int stockId,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == indexId, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (marketIndex.IsArchived)
        {
            return Conflict("Нельзя обновлять историю акций для архивного индекса.");
        }

        var membership = await _context.StockMarketIndices
            .AsNoTracking()
            .Include(x => x.Stock)
            .FirstOrDefaultAsync(
                x => x.MarketIndexId == indexId && x.StockId == stockId && x.EffectiveTo == null,
                cancellationToken);

        if (membership?.Stock is null)
        {
            return NotFound("Акция не входит в текущий состав выбранного индекса.");
        }

        var stock = membership.Stock;
        if (!TryValidateTickerAndExchange(stock, out var validationError))
        {
            return BadRequest(validationError);
        }

        var enqueueResult = _constituentHistoryRefreshJobs.Enqueue(indexId, stockId);
        if (enqueueResult.Status == IndexConstituentHistoryRefreshJobEnqueueStatus.QueueFull)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Очередь обновления исторических данных перегружена. Повторите попытку позже.");
        }

        if (enqueueResult.Job is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Не удалось создать задачу обновления исторических данных.");
        }

        var response = AttachStatusUrl(enqueueResult.Job);
        return Accepted(response.StatusUrl, response);
    }

    [HttpGet("{indexId:int}/constituents/{stockId:int}/history/refresh-jobs/{jobId}")]
    public ActionResult<IndexConstituentHistoryRefreshJobResponse> GetConstituentHistoryRefreshJobStatus(
        int indexId,
        int stockId,
        string jobId)
    {
        if (!_constituentHistoryRefreshJobs.TryGetJob(indexId, stockId, jobId, out var job) || job is null)
        {
            return NotFound("Задача обновления исторических данных не найдена или уже истекла. Запустите обновление повторно.");
        }

        return Ok(AttachStatusUrl(job));
    }

    [HttpPost("{indexId:int}/constituents/history/refresh")]
    public async Task<ActionResult<IndexConstituentHistoryRefreshBatchResponse>> RefreshConstituentsHistory(
        int indexId,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == indexId, cancellationToken);
        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (marketIndex.IsArchived)
        {
            return Conflict("Нельзя обновлять историю акций для архивного индекса.");
        }

        var batchLock = BatchHistoryRefreshLocks.GetOrAdd(indexId, static _ => new SemaphoreSlim(1, 1));
        if (!await batchLock.WaitAsync(0, cancellationToken))
        {
            return Conflict("Обновление исторических данных для этого индекса уже выполняется.");
        }

        try
        {
            var membershipRows = await _context.StockMarketIndices
                .AsNoTracking()
                .Include(x => x.Stock)
                .Where(x => x.MarketIndexId == indexId && x.EffectiveTo == null)
                .OrderBy(x => x.StockId)
                .ThenBy(x => x.Stock.Ticker)
                .ToListAsync(cancellationToken);
            var currentConstituents = membershipRows
                .GroupBy(x => x.StockId)
                .Select(x => x.First().Stock)
                .ToList();

            var items = new List<IndexConstituentHistoryRefreshItemResponse>();
            var attempted = 0;
            var succeeded = 0;
            var failed = 0;
            var rateLimited = 0;
            var skippedRateLimited = 0;
            var detailsTruncated = false;
            var stopDueToRateLimit = false;

            for (var i = 0; i < currentConstituents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stock = currentConstituents[i];

                if (stopDueToRateLimit)
                {
                    skippedRateLimited++;
                    AppendResult(new IndexConstituentHistoryRefreshItemResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Status = "SkippedRateLimited",
                        Error = "Пропущено из-за общего лимита/паузы у поставщика."
                    });
                    continue;
                }

                attempted++;

                if (!TryValidateTickerAndExchange(stock, out var validationError))
                {
                    failed++;
                    AppendResult(new IndexConstituentHistoryRefreshItemResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Status = "Failed",
                        Error = validationError
                    });
                    continue;
                }

                try
                {
                    var refreshResult = await _stockHistoryService.RefreshHistoryAsync(stock, cancellationToken);
                    if (refreshResult.RateLimited)
                    {
                        rateLimited++;
                        stopDueToRateLimit = true;
                        AppendResult(new IndexConstituentHistoryRefreshItemResponse
                        {
                            StockId = stock.Id,
                            Ticker = stock.Ticker,
                            Exchange = stock.Exchange,
                            Status = "RateLimited",
                            DeletedPoints = refreshResult.DeletedPoints,
                            ImportedPoints = refreshResult.ImportedPoints,
                            Error = "Поставщик временно ограничил запросы."
                        });
                        continue;
                    }

                    succeeded++;
                    AppendResult(new IndexConstituentHistoryRefreshItemResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Status = "Succeeded",
                        DeletedPoints = refreshResult.DeletedPoints,
                        ImportedPoints = refreshResult.ImportedPoints
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    failed++;
                    AppendResult(new IndexConstituentHistoryRefreshItemResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Status = "Failed",
                        Error = ex.Message
                    });
                }
                catch (Exception ex)
                {
                    failed++;
                    AppendResult(new IndexConstituentHistoryRefreshItemResponse
                    {
                        StockId = stock.Id,
                        Ticker = stock.Ticker,
                        Exchange = stock.Exchange,
                        Status = "Failed",
                        Error = "Не удалось обновить исторические данные."
                    });
                    _logger.LogWarning(ex, "Batch constituent history refresh failed: indexId={IndexId}, stockId={StockId}", indexId, stock.Id);
                }
            }

            _logger.LogInformation(
                "Batch constituent history refresh completed: indexId={IndexId}, total={Total}, attempted={Attempted}, succeeded={Succeeded}, failed={Failed}, rateLimited={RateLimited}, skippedRateLimited={SkippedRateLimited}, stoppedDueToRateLimit={StoppedDueToRateLimit}",
                indexId,
                currentConstituents.Count,
                attempted,
                succeeded,
                failed,
                rateLimited,
                skippedRateLimited,
                stopDueToRateLimit);

            return Ok(new IndexConstituentHistoryRefreshBatchResponse
            {
                MarketIndexId = indexId,
                Total = currentConstituents.Count,
                Attempted = attempted,
                Succeeded = succeeded,
                Failed = failed,
                RateLimited = rateLimited,
                SkippedRateLimited = skippedRateLimited,
                StoppedDueToRateLimit = stopDueToRateLimit,
                DetailsTruncated = detailsTruncated,
                Results = items
            });

            void AppendResult(IndexConstituentHistoryRefreshItemResponse result)
            {
                if (items.Count < MaxBatchResultDetails)
                {
                    items.Add(result);
                    return;
                }

                detailsTruncated = true;
            }
        }
        finally
        {
            batchLock.Release();
            if (batchLock.CurrentCount == 1)
            {
                BatchHistoryRefreshLocks.TryRemove(new KeyValuePair<int, SemaphoreSlim>(indexId, batchLock));
            }
        }
    }

    private static bool TryValidateTickerAndExchange(Stock stock, out string? validationError)
    {
        if (string.IsNullOrWhiteSpace(stock.Ticker))
        {
            validationError = "У акции должен быть указан тикер для обновления исторических данных.";
            return false;
        }

        if (!StockExchanges.TryNormalize(stock.Exchange, out var normalizedExchange))
        {
            validationError = "У акции указана некорректная биржа для обновления исторических данных.";
            return false;
        }

        stock.Exchange = normalizedExchange;
        validationError = null;
        return true;
    }

    private static bool IsSupportedStockHistoryRange(string normalizedRange)
        => normalizedRange is "5y" or "3y" or "1y" or "6m" or "3m" or "1m" or "1w" or "24h" or "today";


    private static string? GetNonEmptyTrimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [HttpPost("{indexId:int}/constituents/quotes/refresh")]
    public async Task<ActionResult<IndexConstituentsBatchQuoteRefreshJobResponse>> StartConstituentsBatchQuoteRefresh(
        int indexId,
        CancellationToken cancellationToken = default)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == indexId, cancellationToken);

        if (marketIndex is null)
        {
            return NotFound("Индекс не найден.");
        }

        if (marketIndex.IsArchived)
        {
            return UnprocessableEntity("Нельзя обновлять цены для архивного индекса.");
        }

        var hasConstituents = await _context.StockMarketIndices
            .AnyAsync(x => x.MarketIndexId == indexId && x.EffectiveTo == null, cancellationToken);

        if (!hasConstituents)
        {
            return UnprocessableEntity("У индекса нет текущих компонентов для обновления цен.");
        }

        var enqueueResult = _constituentsBatchQuoteRefreshJobs.Enqueue(indexId);

        if (enqueueResult.Status == IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.QueueFull)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Очередь задач заполнена. Попробуйте позже.");
        }

        if (enqueueResult.Job is null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Не удалось создать задачу.");
        }

        var job = AttachBatchQuoteStatusUrl(enqueueResult.Job);

        return Accepted(job.StatusUrl, job);
    }

    [HttpGet("{indexId:int}/constituents/quotes/refresh-jobs/{jobId}")]
    [ActionName(nameof(GetConstituentsBatchQuoteRefreshJobStatus))]
    public ActionResult<IndexConstituentsBatchQuoteRefreshJobResponse> GetConstituentsBatchQuoteRefreshJobStatus(
        int indexId,
        string jobId)
    {
        if (!_constituentsBatchQuoteRefreshJobs.TryGetJob(indexId, jobId, out var job) || job is null)
        {
            return NotFound("Задача не найдена или её срок действия истёк.");
        }

        return Ok(job);
    }

    private IndexConstituentsBatchQuoteRefreshJobResponse AttachBatchQuoteStatusUrl(
        IndexConstituentsBatchQuoteRefreshJobResponse job)
    {
        var fallback = $"/api/market-indices/{job.MarketIndexId}/constituents/quotes/refresh-jobs/{job.JobId}";
        var statusUrl = Url is null
            ? fallback
            : Url.ActionLink(
                nameof(GetConstituentsBatchQuoteRefreshJobStatus),
                values: new { indexId = job.MarketIndexId, jobId = job.JobId }) ?? fallback;

        return new IndexConstituentsBatchQuoteRefreshJobResponse
        {
            JobId = job.JobId,
            MarketIndexId = job.MarketIndexId,
            State = job.State,
            ReusedActiveJob = job.ReusedActiveJob,
            StatusUrl = statusUrl,
            CreatedAtUtc = job.CreatedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            ExpiresAtUtc = job.ExpiresAtUtc,
            Total = job.Total,
            Processed = job.Processed,
            Remaining = job.Remaining,
            Succeeded = job.Succeeded,
            Delayed = job.Delayed,
            NoEurConversion = job.NoEurConversion,
            StaleRejected = job.StaleRejected,
            ProviderFailed = job.ProviderFailed,
            PersistFailed = job.PersistFailed,
            RateLimited = job.RateLimited,
            RateLimitRetries = job.RateLimitRetries,
            RateLimitedSkipped = job.RateLimitedSkipped,
            IsWaitingForRetry = job.IsWaitingForRetry,
            NextRetryAtUtc = job.NextRetryAtUtc,
            Error = job.Error,
        };
    }

    private IndexConstituentHistoryRefreshJobResponse AttachStatusUrl(IndexConstituentHistoryRefreshJobResponse job)
    {
        var fallbackStatusUrl = $"/api/market-indices/{job.MarketIndexId}/constituents/{job.StockId}/history/refresh-jobs/{job.JobId}";
        var statusUrl = Url is null
            ? fallbackStatusUrl
            : Url.ActionLink(
                nameof(GetConstituentHistoryRefreshJobStatus),
                values: new { indexId = job.MarketIndexId, stockId = job.StockId, jobId = job.JobId }) ?? fallbackStatusUrl;

        return new()
        {
            JobId = job.JobId,
            MarketIndexId = job.MarketIndexId,
            StockId = job.StockId,
            State = job.State,
            ReusedActiveJob = job.ReusedActiveJob,
            StatusUrl = statusUrl,
            CreatedAtUtc = job.CreatedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            ExpiresAtUtc = job.ExpiresAtUtc,
            DeletedPoints = job.DeletedPoints,
            ImportedPoints = job.ImportedPoints,
            Error = job.Error
        };
    }

    private static IndexConstituentsRefreshResponse CreateRefreshResponse(int marketIndexId, IndexConstituentsResult providerResult)
        => new()
        {
            MarketIndexId = marketIndexId,
            ProviderStatus = providerResult.Status.ToString(),
            ProviderName = providerResult.ProviderName,
            ProviderMessage = providerResult.Message,
            FetchedAt = providerResult.FetchedAt,
            AsOfDate = providerResult.AsOfDate,
            SourceUrl = providerResult.SourceUrl,
            IsCuratedSnapshot = providerResult.IsCuratedSnapshot,
            IsStale = providerResult.IsStale,
            Added = 0,
            Updated = 0,
            Unchanged = 0,
            Closed = 0,
            Conflicts = 0,
        };

    private async Task<MarketIndexDto> LoadMarketIndexAsync(int id)
    {
        var marketIndex = await _context.MarketIndices
            .AsNoTracking()
            .FirstAsync(x => x.Id == id);

        return MapMarketIndex(marketIndex);
    }

    private static MarketIndexDto MapMarketIndex(MarketIndex marketIndex)
        => new()
        {
            Id = marketIndex.Id,
            Name = marketIndex.Name,
            NormalizedName = marketIndex.NormalizedName,
            Code = marketIndex.Code,
            NormalizedCode = marketIndex.NormalizedCode,
            ProviderSymbol = marketIndex.ProviderSymbol,
            Description = marketIndex.Description,
            CountryOrRegion = marketIndex.CountryOrRegion,
            SortOrder = marketIndex.SortOrder,
            IsArchived = marketIndex.IsArchived,
            ShowInNavigation = marketIndex.ShowInNavigation,
            CreatedAt = marketIndex.CreatedAt,
            UpdatedAt = marketIndex.UpdatedAt
        };

    private static string? NormalizeConstituentClassification(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static string? NormalizeProviderSymbol(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool CanApplySectorToStock(Stock stock, int sectorId, IReadOnlyDictionary<int, int> industrySectorMap)
    {
        if (stock.IndustryId is not int industryId)
        {
            return true;
        }

        return !industrySectorMap.TryGetValue(industryId, out var industrySectorId)
               || industrySectorId == sectorId;
    }

    private static int? GetEffectiveSectorId(Stock stock, IReadOnlyDictionary<int, int> industrySectorMap)
    {
        if (stock.IndustryId is int industryId
            && industrySectorMap.TryGetValue(industryId, out var industrySectorId))
        {
            return industrySectorId;
        }

        return stock.SectorId;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
