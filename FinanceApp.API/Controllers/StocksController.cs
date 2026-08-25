using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Data.Data;
using FinanceApp.Core.Models;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StocksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IStockHistoryService _stockHistoryService;
    private readonly IStockPerformanceCalculationService _stockPerformanceCalculationService;
    private readonly StockQuoteSnapshotPersistenceService _stockQuoteSnapshotPersistenceService;
    private readonly IStockMetadataEnrichmentService? _stockMetadataEnrichmentService;
    private readonly ILogger<StocksController> _logger;

    public StocksController(
        AppDbContext context,
        IStockHistoryService stockHistoryService,
        IStockPerformanceCalculationService stockPerformanceCalculationService,
        StockQuoteSnapshotPersistenceService stockQuoteSnapshotPersistenceService,
        ILogger<StocksController> logger,
        IStockMetadataEnrichmentService? stockMetadataEnrichmentService = null)
    {
        _context = context;
        _stockHistoryService = stockHistoryService;
        _stockPerformanceCalculationService = stockPerformanceCalculationService;
        _stockQuoteSnapshotPersistenceService = stockQuoteSnapshotPersistenceService;
        _stockMetadataEnrichmentService = stockMetadataEnrichmentService;
        _logger = logger;
    }

    /// <summary>Normalizes WKN/ISIN: trim whitespace, uppercase; blank becomes null.</summary>
    private static string? NormalizeIdentifier(string? value) => StockIdentifiers.Normalize(value);

    /// <summary>Validates a finanzen.net slug. Returns a 400 result when invalid, otherwise null.</summary>
    private ActionResult? ValidateFinanzenNetSlug(string? slug)
    {
        if (slug is null)
        {
            return null;
        }

        if (!FinanzenNetQuoteService.IsValidSlug(slug))
        {
            return BadRequest("FinanzenNetSlug darf nur Kleinbuchstaben, Ziffern, Bindestriche und Unterstriche enthalten und muss mit einem Buchstaben oder einer Ziffer beginnen.");
        }

        return null;
    }

    private ActionResult? NormalizeAndValidateStock(Stock stock)
    {
        stock.Wkn = NormalizeIdentifier(stock.Wkn);
        stock.Isin = NormalizeIdentifier(stock.Isin);
        stock.ProviderSymbol = string.IsNullOrWhiteSpace(stock.ProviderSymbol)
            ? null
            : stock.ProviderSymbol.Trim();
        stock.FinanzenNetSlug = string.IsNullOrWhiteSpace(stock.FinanzenNetSlug)
            ? null
            : stock.FinanzenNetSlug.Trim();
        stock.Name = (stock.Name ?? string.Empty).Trim();
        stock.CommonName = string.IsNullOrWhiteSpace(stock.CommonName)
            ? stock.Name
            : stock.CommonName.Trim();

        if (!StockExchanges.TryNormalize(stock.Exchange, out var normalizedExchange))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(stock.Exchange)] = [$"Exchange must be one of: {string.Join(", ", StockExchanges.Supported)}."]
            }));
        }

        stock.Exchange = normalizedExchange;

        var slugError = ValidateFinanzenNetSlug(stock.FinanzenNetSlug);
        if (slugError is not null)
        {
            return slugError;
        }

        return ValidateIdentifiers(stock.Wkn, stock.Isin);
    }

    /// <summary>Validates WKN and ISIN formats. Returns a 400 result when invalid, otherwise null.</summary>
    private ActionResult? ValidateIdentifiers(string? wkn, string? isin)
    {
        if (wkn != null && !StockIdentifiers.IsValidWkn(wkn))
            return BadRequest("WKN должен содержать ровно 6 буквенно-цифровых символов (A–Z, 0–9).");
        if (isin != null && !StockIdentifiers.IsValidIsin(isin))
            return BadRequest("ISIN должен содержать ровно 12 символов: 2 буквы страны и 10 буквенно-цифровых символов (A–Z, 0–9).");
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Stock>>> GetAll([FromQuery] bool includeCatalog = false)
    {
        var query = _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .ThenInclude(x => x.MarketIndex)
            .AsQueryable();

        if (!includeCatalog)
        {
            query = query.Where(s => s.TrackingStatus == StockTrackingStatus.Tracked);
        }

        return PrepareStocksForResponse(await query.ToListAsync());
    }

    [HttpGet("tracked")]
    public Task<ActionResult<IEnumerable<Stock>>> GetTracked()
        => GetAll(includeCatalog: false);

    [HttpGet("catalog")]
    public Task<ActionResult<IEnumerable<Stock>>> GetCatalog()
        => GetAll(includeCatalog: true);

    [HttpGet("catalog/performance")]
    public async Task<ActionResult<StockCatalogPerformanceResponse>> GetCatalogPerformance(
        [FromQuery] string range = "1y",
        CancellationToken cancellationToken = default)
    {
        var normalizedRange = (range ?? string.Empty).Trim().ToLowerInvariant();
        if (!_stockPerformanceCalculationService.IsSupportedRange(normalizedRange))
        {
            return BadRequest("Недопустимый диапазон. Допустимые значения: 5y, 3y, 1y, 6m, 3m, 1m, 1w, 24h, today");
        }

        var stocks = await _context.Stocks
            .AsNoTracking()
            .Select(x => new StockPerformanceSubject(
                x.Id,
                x.Exchange,
                x.CurrentPrice,
                x.CurrentPriceChange,
                x.CurrentPriceChangePercent,
                x.CurrentPriceAt))
            .ToListAsync(cancellationToken);

        if (stocks.Count == 0)
        {
            return Ok(new StockCatalogPerformanceResponse
            {
                Range = normalizedRange,
                GeneratedAtUtc = DateTime.UtcNow,
                Items = Array.Empty<IndexConstituentPerformanceItemDto>(),
            });
        }

        var items = await _stockPerformanceCalculationService.CalculateAsync(stocks, normalizedRange, cancellationToken);

        return Ok(new StockCatalogPerformanceResponse
        {
            Range = normalizedRange,
            GeneratedAtUtc = DateTime.UtcNow,
            Items = items,
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Stock>> GetById(int id)
    {
        var stock = await _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .ThenInclude(x => x.MarketIndex)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (stock == null) return NotFound();
        return PrepareStockForResponse(stock);
    }

    [HttpGet("{id}/technical-analysis")]
    public async Task<ActionResult<TechnicalAnalysisResponse>> GetTechnicalAnalysis(
        int id,
        [FromServices] IStockTechnicalAnalysisService technicalAnalysisService,
        CancellationToken cancellationToken = default)
    {
        var analysis = await technicalAnalysisService.GetTechnicalAnalysisAsync(id, cancellationToken);
        if (analysis is null)
        {
            return NotFound();
        }

        return Ok(analysis);
    }

    [HttpGet("{id}/history")]
    public async Task<ActionResult> GetHistory(int id, [FromQuery] string range = "5y", CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (stock == null)
        {
            return NotFound();
        }

        var normalizedRange = (range ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedRange is not ("5y" or "3y" or "1y" or "6m" or "3m" or "1m" or "1w" or "24h" or "today"))
        {
            return BadRequest("Invalid range. Allowed values: 5y, 3y, 1y, 6m, 3m, 1m, 1w, 24h, today");
        }

        return Ok(await _stockHistoryService.GetHistoryAsync(stock, normalizedRange, cancellationToken));
    }


    [HttpPost("{id}/history/refresh")]
    public async Task<ActionResult<StockHistoryRefreshResponse>> RefreshHistory(int id, CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (stock == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(stock.Ticker))
        {
            return BadRequest("У акции должен быть указан тикер для перезагрузки истории.");
        }

        if (!StockExchanges.TryNormalize(stock.Exchange, out var normalizedExchange))
        {
            return BadRequest("У акции указана некорректная биржа для перезагрузки истории.");
        }

        stock.Exchange = normalizedExchange;

        try
        {
            return Ok(await _stockHistoryService.RefreshHistoryAsync(stock, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id}/history/routing-diagnostics")]
    public async Task<ActionResult<StockHistoryRepairDiagnosticsResponse>> GetHistoryRoutingDiagnostics(
        int id,
        CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (stock == null)
        {
            return NotFound();
        }

        return Ok(await _stockHistoryService.GetRepairDiagnosticsAsync(stock, cancellationToken));
    }

    [HttpPost("{id}/history/provider-symbol/validate")]
    public async Task<ActionResult<StockHistoryRepairDiagnosticsResponse>> ValidateHistoryProviderSymbol(
        int id,
        [FromBody] StockHistoryProviderSymbolValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (stock == null)
        {
            return NotFound();
        }

        var result = await _stockHistoryService.ValidateProviderSymbolAsync(stock, request?.CandidateProviderSymbol, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id}/history/hard-reset")]
    public async Task<ActionResult<StockHistoryRepairDiagnosticsResponse>> HardResetHistory(
        int id,
        [FromBody] StockHistoryHardResetRequest request,
        CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (stock == null)
        {
            return NotFound();
        }

        var confirmation = request?.ConfirmationText?.Trim();
        var isConfirmed =
            string.Equals(confirmation, "УДАЛИТЬ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(confirmation, stock.Ticker, StringComparison.OrdinalIgnoreCase);
        if (!isConfirmed)
        {
            return BadRequest("Подтверждение не пройдено. Введите тикер или слово «УДАЛИТЬ».");
        }

        var result = await _stockHistoryService.HardResetHistoryAsync(stock, request?.CandidateProviderSymbol, cancellationToken);
        if (!result.ResetPerformed)
        {
            return Ok(result);
        }

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<Stock>> Create(Stock stock)
    {
        var validationError = NormalizeAndValidateStock(stock);
        if (validationError != null) return validationError;

        var (classificationValidationError, sectorId, industryId) = await ResolveClassificationAssignmentAsync(
            stock.SectorId,
            stock.IndustryId);
        if (classificationValidationError != null) return classificationValidationError;

        var requestedMarketIndexIds = stock.MarketIndexIds;
        var (marketIndicesValidationError, marketIndices) = await ValidateMarketIndexAssignmentsAsync(requestedMarketIndexIds);
        if (marketIndicesValidationError != null) return marketIndicesValidationError;

        var duplicateError = await ValidateCreateUniquenessAsync(stock);
        if (duplicateError != null) return duplicateError;

        stock.UpdatedAt = DateTime.UtcNow;
        // Standard create always produces a Tracked stock; CatalogOnly is set only by import jobs.
        stock.TrackingStatus = StockTrackingStatus.Tracked;
        stock.Industry = null;
        stock.IndustryId = industryId;
        stock.SectorId = sectorId;
        var now = DateTime.UtcNow;
        stock.MarketIndices = marketIndices
            .Select(marketIndex => new StockMarketIndex
            {
                MarketIndexId = marketIndex.Id,
                Stock = stock,
                Source = "Manual",
                ImportedAt = now,
            })
            .ToList();
        _context.Stocks.Add(stock);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            return BadRequest(BuildCreateDuplicateMessage(stock.Wkn, stock.ProviderSymbol, stock.Ticker, stock.Exchange));
        }

        if (_stockMetadataEnrichmentService is not null)
        {
            try
            {
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                await _stockMetadataEnrichmentService.EnqueueSelectedAsync([stock.Id], userId, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue metadata enrichment for stock {StockId}", stock.Id);
            }
        }

        try
        {
            await _stockHistoryService.SyncHistoricalDataForStockAsync(stock, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stock created but history sync failed for stock {StockId}", stock.Id);
        }

        var createdStock = await LoadStockWithClassificationAsync(stock.Id);
        return CreatedAtAction(nameof(GetById), new { id = stock.Id }, createdStock);
    }

    /// <summary>
    /// Legacy full-object update endpoint. Returns 410 Gone — callers must migrate to
    /// PUT /api/Stocks/{id}/metadata (editable fields) and PATCH /api/Stocks/{id}/quote (price).
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult Update(int id)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        var userAgent = Request.Headers.UserAgent.ToString();
        _logger.LogWarning(
            "Legacy PUT /api/Stocks/{StockId} rejected (410 Gone). UserId={UserId} UserAgent={UserAgent}",
            id, userId, userAgent);

        return StatusCode(StatusCodes.Status410Gone,
            "PUT /api/Stocks/{id} has been retired. " +
            "Use PUT /api/Stocks/{id}/metadata to update editable fields " +
            "and PATCH /api/Stocks/{id}/quote to update quote data.");
    }

    /// <summary>
    /// Updates editable metadata fields for an existing stock.
    /// Ticker and Exchange are identity fields and cannot be changed after creation.
    /// </summary>
    [HttpPut("{id}/metadata")]
    public async Task<IActionResult> UpdateMetadata(int id, UpdateStockMetadataRequest request)
    {
        var existing = await _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .FirstOrDefaultAsync(s => s.Id == id);
        if (existing == null) return NotFound();

        // Normalize fields the same way as Create
        var wkn = NormalizeIdentifier(request.Wkn);
        var isin = NormalizeIdentifier(request.Isin);
        var finanzenNetSlug = string.IsNullOrWhiteSpace(request.FinanzenNetSlug)
            ? null
            : request.FinanzenNetSlug.Trim();
        var name = (request.Name ?? string.Empty).Trim();
        var commonName = string.IsNullOrWhiteSpace(request.CommonName) ? name : request.CommonName.Trim();

        var slugError = ValidateFinanzenNetSlug(finanzenNetSlug);
        if (slugError is not null) return slugError;

        var identifierError = ValidateIdentifiers(wkn, isin);
        if (identifierError is not null) return identifierError;

        var (classificationValidationError, sectorId, industryId) = await ResolveClassificationAssignmentAsync(
            request.SectorId,
            request.IndustryId,
            existing.SectorId,
            existing.IndustryId);
        if (classificationValidationError != null) return classificationValidationError;

        var currentMarketIndexIds = existing.MarketIndices.Select(x => x.MarketIndexId).ToHashSet();
        var (marketIndicesValidationError, marketIndices) = await ValidateMarketIndexAssignmentsAsync(request.MarketIndexIds, currentMarketIndexIds);
        if (marketIndicesValidationError != null) return marketIndicesValidationError;

        existing.Name = name;
        existing.CommonName = commonName;
        existing.Wkn = wkn;
        existing.Isin = isin;
        existing.FinanzenNetSlug = finanzenNetSlug;
        existing.IndustryId = industryId;
        existing.SectorId = sectorId;
        existing.UpdatedAt = DateTime.UtcNow;
        SyncMarketIndices(existing, request.MarketIndexIds, marketIndices);

        // Manual price edit: clear stale snapshot fields so the UI never shows outdated
        // change/timestamp alongside a manually entered price.
        existing.CurrentPrice = request.CurrentPrice;
        existing.CurrentPriceChange = null;
        existing.CurrentPriceChangePercent = null;
        existing.CurrentPriceAt = null;
        existing.CurrentPriceIsDelayed = false;
        existing.CurrentPriceDelayWarning = null;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            return BadRequest("Нарушено ограничение уникальности. Проверьте тикер/биржу и provider symbol.");
        }

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        var userAgent = Request.Headers.UserAgent.ToString();
        _logger.LogInformation(
            "Stock metadata updated. StockId={StockId} Ticker={Ticker} Exchange={Exchange} UserId={UserId} UserAgent={UserAgent}",
            id, existing.Ticker, existing.Exchange, userId, userAgent);

        return NoContent();
    }

    private static UpdateStockQuoteResponse BuildQuoteResponse(int stockId, Stock stock, bool applied) => new()
    {
        StockId = stockId,
        CurrentPrice = stock.CurrentPrice,
        CurrentPriceChange = stock.CurrentPriceChange,
        CurrentPriceChangePercent = stock.CurrentPriceChangePercent,
        CurrentPriceAt = stock.CurrentPriceAt,
        CurrentPriceIsDelayed = stock.CurrentPriceIsDelayed,
        CurrentPriceDelayWarning = stock.CurrentPriceDelayWarning,
        SnapshotApplied = applied,
        HistoryApplied = false,
        Applied = applied,
    };

    [HttpPatch("{id}/quote")]
    public async Task<ActionResult<UpdateStockQuoteResponse>> UpdateQuote(int id, UpdateStockQuoteRequest request)
    {
        var persistenceResult = await _stockQuoteSnapshotPersistenceService.ApplyAsync(
            id,
            new PersistStockQuoteSnapshotRequest
            {
                CurrentPrice = request.CurrentPrice,
                CurrentPriceChange = request.CurrentPriceChange,
                CurrentPriceChangePercent = request.CurrentPriceChangePercent,
                CurrentPriceAt = request.CurrentPriceAt,
                CurrentPriceIsDelayed = request.CurrentPriceIsDelayed,
                CurrentPriceDelayWarning = request.CurrentPriceDelayWarning,
                QuoteCurrency = "EUR",
                FinancialCurrency = "EUR",
                NormalizedQuoteCurrency = "EUR",
                QuoteUnitMultiplier = 1m,
            });

        if (!persistenceResult.StockFound)
        {
            return NotFound();
        }

        if (!persistenceResult.Applied)
        {
            _logger.LogInformation(
                "Skipping stock quote update. StockId={StockId} Reason={Reason}",
                id,
                persistenceResult.Reason);
        }

        return Ok(new UpdateStockQuoteResponse
        {
            StockId = id,
            CurrentPrice = persistenceResult.CurrentPrice,
            CurrentPriceChange = persistenceResult.CurrentPriceChange,
            CurrentPriceChangePercent = persistenceResult.CurrentPriceChangePercent,
            CurrentPriceAt = persistenceResult.CurrentPriceAt,
            CurrentPriceIsDelayed = persistenceResult.CurrentPriceIsDelayed,
            CurrentPriceDelayWarning = persistenceResult.CurrentPriceDelayWarning,
            SnapshotApplied = persistenceResult.SnapshotApplied,
            HistoryApplied = persistenceResult.HistoryApplied,
            Applied = persistenceResult.Applied,
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var stock = await _context.Stocks
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .FirstOrDefaultAsync(s => s.Id == id);
        if (stock == null) return NotFound();

        var isReferenced = await _context.PortfolioItems.AnyAsync(item => item.StockId == id);
        if (isReferenced)
        {
            return Conflict("Невозможно удалить акцию: она используется как минимум в одном портфеле.");
        }

        // If still a current constituent of at least one index, demote to CatalogOnly instead of deleting.
        var hasActiveIndexMembership = stock.MarketIndices.Any();
        if (hasActiveIndexMembership && stock.TrackingStatus == StockTrackingStatus.Tracked)
        {
            stock.TrackingStatus = StockTrackingStatus.CatalogOnly;
            await _context.SaveChangesAsync();
            return NoContent();
        }

        _context.Stocks.Remove(stock);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Promotes a CatalogOnly stock to Tracked status.
    /// If the stock is already Tracked, returns 200 without changes.
    /// Triggers history sync after promotion.
    /// </summary>
    [HttpPost("{id}/track")]
    public async Task<ActionResult<Stock>> Track(int id, CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .ThenInclude(x => x.MarketIndex)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (stock == null) return NotFound();

        if (stock.TrackingStatus != StockTrackingStatus.Tracked)
        {
            stock.TrackingStatus = StockTrackingStatus.Tracked;
            stock.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            try
            {
                await _stockHistoryService.SyncHistoricalDataForStockAsync(stock, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stock promoted but history sync failed for stock {StockId}", stock.Id);
            }
        }

        return PrepareStockForResponse(stock);
    }

    /// <summary>
    /// Demotes a Tracked stock to CatalogOnly without deleting the stock record.
    /// If the stock is already CatalogOnly, returns 200 without changes.
    /// </summary>
    [HttpPost("{id}/untrack")]
    public async Task<ActionResult<Stock>> Untrack(int id, CancellationToken cancellationToken = default)
    {
        var stock = await _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .ThenInclude(x => x.MarketIndex)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (stock == null) return NotFound();

        if (stock.TrackingStatus != StockTrackingStatus.CatalogOnly)
        {
            stock.TrackingStatus = StockTrackingStatus.CatalogOnly;
            stock.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return PrepareStockForResponse(stock);
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<ActionResult?> ValidateCreateUniquenessAsync(Stock stock)
    {
        var duplicates = await _context.Stocks
            .AsNoTracking()
            .Where(x =>
                (stock.ProviderSymbol != null && x.ProviderSymbol == stock.ProviderSymbol) ||
                (x.Ticker == stock.Ticker && x.Exchange == stock.Exchange))
            .ToListAsync();

        if (stock.ProviderSymbol != null && duplicates.Any(x => x.ProviderSymbol == stock.ProviderSymbol))
        {
            return BadRequest(BuildProviderSymbolDuplicateMessage(stock.ProviderSymbol));
        }

        if (duplicates.Any(x => x.Ticker == stock.Ticker && x.Exchange == stock.Exchange))
        {
            return BadRequest(BuildListingDuplicateMessage(stock.Ticker, stock.Exchange));
        }

        return null;
    }

    private async Task<Stock> LoadStockWithClassificationAsync(int id)
        => await _context.Stocks
            .Include(s => s.Sector)
            .Include(s => s.Industry)
            .ThenInclude(i => i!.Sector)
            .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
            .ThenInclude(x => x.MarketIndex)
            .FirstAsync(s => s.Id == id);

    private async Task<(ActionResult? Error, Industry? Industry)> ValidateIndustryAssignmentAsync(int? industryId, int? currentIndustryId = null)
    {
        if (industryId is null)
        {
            return (null, null);
        }

        var industry = await _context.Industries
            .Include(i => i.Sector)
            .FirstOrDefaultAsync(i => i.Id == industryId.Value);

        if (industry is null)
        {
            return (BadRequest("Указанная отрасль не найдена."), null);
        }

        // Allow existing archived bindings to remain unchanged during metadata edits.
        if (industry.Id == currentIndustryId)
        {
            return (null, industry);
        }

        if (industry.IsArchived)
        {
            return (BadRequest("Нельзя привязать акцию к архивной отрасли."), null);
        }

        if (industry.Sector.IsArchived)
        {
            return (BadRequest("Нельзя привязать акцию к отрасли из архивного сектора."), null);
        }

        return (null, industry);
    }

    private async Task<(ActionResult? Error, int? SectorId, int? IndustryId)> ResolveClassificationAssignmentAsync(
        int? requestedSectorId,
        int? requestedIndustryId,
        int? currentSectorId = null,
        int? currentIndustryId = null)
    {
        var (industryValidationError, industry) = await ValidateIndustryAssignmentAsync(requestedIndustryId, currentIndustryId);
        if (industryValidationError != null)
        {
            return (industryValidationError, null, null);
        }

        if (industry is not null)
        {
            return (null, industry.SectorId, industry.Id);
        }

        if (requestedSectorId is null)
        {
            return (null, null, null);
        }

        var sector = await _context.Sectors.FirstOrDefaultAsync(x => x.Id == requestedSectorId.Value);
        if (sector is null)
        {
            return (BadRequest("Указанный сектор не найден."), null, null);
        }

        // Allow existing archived bindings to remain unchanged during metadata edits.
        if (sector.IsArchived && sector.Id != currentSectorId)
        {
            return (BadRequest("Нельзя привязать акцию к архивному сектору."), null, null);
        }

        return (null, sector.Id, null);
    }

    private async Task<(ActionResult? Error, List<MarketIndex> MarketIndices)> ValidateMarketIndexAssignmentsAsync(
        List<int>? marketIndexIds,
        ISet<int>? currentMarketIndexIds = null)
    {
        if (marketIndexIds is null)
        {
            return (null, new List<MarketIndex>());
        }

        var distinctIds = marketIndexIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return (null, new List<MarketIndex>());
        }

        var marketIndices = await _context.MarketIndices
            .Where(x => distinctIds.Contains(x.Id))
            .ToListAsync();

        if (marketIndices.Count != distinctIds.Length)
        {
            return (BadRequest("Указан несуществующий мировой индекс."), new List<MarketIndex>());
        }

        currentMarketIndexIds ??= new HashSet<int>();

        if (marketIndices.Any(x => x.IsArchived && !currentMarketIndexIds.Contains(x.Id)))
        {
            return (BadRequest("Нельзя привязать акцию к архивному мировому индексу."), new List<MarketIndex>());
        }

        return (null, marketIndices);
    }

    private void SyncMarketIndices(Stock stock, List<int>? requestedIds, IReadOnlyCollection<MarketIndex> marketIndices)
    {
        if (requestedIds is null)
        {
            return;
        }

        var requestedIdSet = requestedIds.Distinct().ToHashSet();
        // Only consider current memberships (EffectiveTo IS NULL) for the diff.
        var currentJoins = stock.MarketIndices
            .Where(x => x.EffectiveTo == null)
            .ToDictionary(x => x.MarketIndexId);

        var now = DateTime.UtcNow;

        // Close memberships that are no longer requested (set EffectiveTo instead of deleting).
        foreach (var join in currentJoins.Values.Where(x => !requestedIdSet.Contains(x.MarketIndexId)).ToList())
        {
            join.EffectiveTo = now;
        }

        // Add new memberships for newly requested indices.
        foreach (var marketIndex in marketIndices)
        {
            if (!currentJoins.ContainsKey(marketIndex.Id))
            {
                stock.MarketIndices.Add(new StockMarketIndex
                {
                    StockId = stock.Id,
                    MarketIndexId = marketIndex.Id,
                    Source = "Manual",
                    ImportedAt = now,
                });
            }
        }
    }

    private static List<Stock> PrepareStocksForResponse(List<Stock> stocks)
        => stocks.Select(PrepareStockForResponse).ToList();

    private static Stock PrepareStockForResponse(Stock stock)
    {
        stock.Sector = stock.Industry?.Sector ?? stock.Sector;
        stock.MarketIndexIds = stock.MarketIndices
            .Where(x => x.EffectiveTo == null)
            .OrderBy(x => x.MarketIndex.SortOrder)
            .ThenBy(x => x.MarketIndex.Name)
            .Select(x => x.MarketIndexId)
            .ToList();
        return stock;
    }

    private static string BuildCreateDuplicateMessage(string? wkn, string? providerSymbol, string ticker, string exchange)
    {
        if (providerSymbol != null)
            return BuildProviderSymbolDuplicateMessage(providerSymbol);
        return BuildListingDuplicateMessage(ticker, exchange);
    }

    private static string BuildProviderSymbolDuplicateMessage(string providerSymbol)
        => $"Акция с ProviderSymbol «{providerSymbol}» уже существует.";

    private static string BuildListingDuplicateMessage(string ticker, string exchange)
        => $"Акция с тикером «{ticker}» на бирже «{exchange}» уже существует.";
}
