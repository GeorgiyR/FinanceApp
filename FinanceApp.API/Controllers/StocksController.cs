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
    private readonly IStockDependencyDiagnosticsService _stockDependencyDiagnosticsService;
    private readonly IStockMetadataEnrichmentService? _stockMetadataEnrichmentService;
    private readonly ISystemProcessJournalService? _systemProcessJournalService;
    private readonly ILogger<StocksController> _logger;

    public StocksController(
        AppDbContext context,
        IStockHistoryService stockHistoryService,
        IStockPerformanceCalculationService stockPerformanceCalculationService,
        StockQuoteSnapshotPersistenceService stockQuoteSnapshotPersistenceService,
        ILogger<StocksController> logger,
        IStockDependencyDiagnosticsService? stockDependencyDiagnosticsService = null,
        IStockMetadataEnrichmentService? stockMetadataEnrichmentService = null,
        ISystemProcessJournalService? systemProcessJournalService = null)
    {
        _context = context;
        _stockHistoryService = stockHistoryService;
        _stockPerformanceCalculationService = stockPerformanceCalculationService;
        _stockQuoteSnapshotPersistenceService = stockQuoteSnapshotPersistenceService;
        _stockDependencyDiagnosticsService = stockDependencyDiagnosticsService ?? new StockDependencyDiagnosticsService(context);
        _stockMetadataEnrichmentService = stockMetadataEnrichmentService;
        _systemProcessJournalService = systemProcessJournalService;
        _logger = logger;
    }

    /// <summary>Normalizes WKN/ISIN: trim whitespace, uppercase; blank becomes null.</summary>
    private static string? NormalizeIdentifier(string? value) => StockIdentifiers.Normalize(value);
    private static string NormalizeTicker(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

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

    [HttpPost("history/frankfurt/rebuild-aggregates")]
    public async Task<ActionResult<FrankfurtAggregateRebuildResponse>> RebuildFrankfurtAggregates(
        [FromBody] FrankfurtAggregateRebuildRequest? request,
        CancellationToken cancellationToken = default)
    {
        var batchSize = request?.BatchSize ?? 25;
        var correlationId = HttpContext.TraceIdentifier;
        var processRunId = await (_systemProcessJournalService?.CreateOrGetAsync(new CreateSystemProcessRunRequest
        {
            ProcessType = SystemProcessTypes.FrankfurtAggregateRebuild,
            Trigger = SystemProcessTrigger.ApiRepair,
            InitiatedByUserId = User?.Identity?.Name,
            CorrelationId = correlationId,
            InitialStatus = SystemProcessRunStatus.Running,
            Details = new
            {
                batchSize,
                afterStockId = request?.AfterStockId,
            },
        }, cancellationToken) ?? Task.FromResult(0L));

        try
        {
            var response = await _stockHistoryService.RebuildFrankfurtAggregatesAsync(
                batchSize,
                request?.AfterStockId,
                cancellationToken);

            if (processRunId > 0)
            {
                await _systemProcessJournalService!.CompleteAsync(
                    processRunId,
                    response.Errors.Count > 0 || response.FailedStocks > 0
                        ? SystemProcessRunStatus.CompletedWithErrors
                        : SystemProcessRunStatus.Succeeded,
                    new UpdateSystemProcessRunRequest
                    {
                        TotalItems = response.ProcessedStocks,
                        ProcessedItems = response.ProcessedStocks,
                        SucceededItems = response.RebuiltStocks,
                        FailedItems = response.FailedStocks,
                        ResultSummary = $"Обработано {response.ProcessedStocks}; успешно {response.RebuiltStocks}; ошибок {response.FailedStocks}.",
                        LastProcessedEntity = response.NextAfterStockId.HasValue ? $"afterStockId={response.NextAfterStockId.Value}" : null,
                    },
                    cancellationToken);
            }

            return Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (processRunId > 0)
            {
                await _systemProcessJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Interrupted,
                    new UpdateSystemProcessRunRequest
                    {
                        ErrorSummary = "Пересборка агрегатов прервана остановкой приложения или отменой запроса.",
                    },
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (processRunId > 0)
            {
                await _systemProcessJournalService!.CompleteAsync(
                    processRunId,
                    SystemProcessRunStatus.Failed,
                    new UpdateSystemProcessRunRequest
                    {
                        ErrorSummary = ex.Message,
                    },
                    CancellationToken.None);
            }

            throw;
        }
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
        stock.PurchaseCandidatePriority = StockPurchaseCandidatePriority.None;
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

    [HttpPut("{id}/edit")]
    public async Task<IActionResult> UpdateEdit(
        int id,
        UpdateStockEditRequest request,
        CancellationToken cancellationToken = default)
    {
        var mutationResult = await ExecuteMutationWithExecutionStrategyAsync(async ct =>
        {
            var stock = await _context.Stocks
                .Include(s => s.Sector)
                .Include(s => s.Industry)
                .ThenInclude(i => i!.Sector)
                .Include(s => s.MarketIndices.Where(x => x.EffectiveTo == null))
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (stock is null)
            {
                return new EditMutationResult(ActionResult: NotFound());
            }

            var newTicker = NormalizeTicker(request.Ticker);
            if (string.IsNullOrWhiteSpace(newTicker))
            {
                return new EditMutationResult(ActionResult: BadRequest("Тикер не может быть пустым."));
            }

            if (!StockExchanges.TryNormalize(request.Exchange, out var newExchange))
            {
                return new EditMutationResult(ActionResult: BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [nameof(request.Exchange)] = [$"Exchange must be one of: {string.Join(", ", StockExchanges.Supported)}."]
                })));
            }

            var oldTicker = stock.Ticker;
            var oldExchange = stock.Exchange;
            var identityChanged = !string.Equals(oldTicker, newTicker, StringComparison.Ordinal)
                                  || !string.Equals(oldExchange, newExchange, StringComparison.Ordinal);
            var transitionLabel = $"{oldTicker} ({oldExchange}) → {newTicker} ({newExchange})";
            if (identityChanged && !request.IdentityEditingEnabled)
            {
                return new EditMutationResult(ActionResult: BadRequest("Сначала включите режим «Изменить тикер / биржу»."));
            }

            if (identityChanged
                && !string.Equals(request.ConfirmationText?.Trim(), transitionLabel, StringComparison.OrdinalIgnoreCase))
            {
                return new EditMutationResult(ActionResult: BadRequest($"Подтверждение не пройдено. Введите точно: {transitionLabel}"));
            }

            var wkn = NormalizeIdentifier(request.Wkn);
            var isin = NormalizeIdentifier(request.Isin);
            var finanzenNetSlug = string.IsNullOrWhiteSpace(request.FinanzenNetSlug)
                ? null
                : request.FinanzenNetSlug.Trim();

            // On identity change, unchanged identifiers belong to the old instrument and are cleared.
            if (identityChanged)
            {
                if (string.Equals(wkn, NormalizeIdentifier(stock.Wkn), StringComparison.Ordinal))
                {
                    wkn = null;
                }

                if (string.Equals(isin, NormalizeIdentifier(stock.Isin), StringComparison.Ordinal))
                {
                    isin = null;
                }

                if (string.Equals(finanzenNetSlug, stock.FinanzenNetSlug?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    finanzenNetSlug = null;
                }
            }

            var slugError = ValidateFinanzenNetSlug(finanzenNetSlug);
            if (slugError is not null)
            {
                return new EditMutationResult(ActionResult: slugError);
            }

            var identifierError = ValidateIdentifiers(wkn, isin);
            if (identifierError is not null)
            {
                return new EditMutationResult(ActionResult: identifierError);
            }

            var (classificationValidationError, sectorId, industryId) = await ResolveClassificationAssignmentAsync(
                request.SectorId,
                request.IndustryId,
                stock.SectorId,
                stock.IndustryId);
            if (classificationValidationError != null)
            {
                return new EditMutationResult(ActionResult: classificationValidationError);
            }

            var currentMarketIndexIds = stock.MarketIndices.Select(x => x.MarketIndexId).ToHashSet();
            var (marketIndicesValidationError, marketIndices) = await ValidateMarketIndexAssignmentsAsync(request.MarketIndexIds, currentMarketIndexIds);
            if (marketIndicesValidationError != null)
            {
                return new EditMutationResult(ActionResult: marketIndicesValidationError);
            }

            if (identityChanged)
            {
                var diagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
                if (diagnostics.HasBlockers)
                {
                    return new EditMutationResult(ActionResult: Conflict(BuildBlockedResponse(
                        "Невозможно изменить тикер/биржу: обнаружены бизнес-зависимости. Исправление идентичности разрешено только для нереференсной акции.",
                        diagnostics)));
                }

                var duplicateListing = await _context.Stocks
                    .AsNoTracking()
                    .AnyAsync(
                        s => s.Id != stock.Id
                             && s.Ticker == newTicker
                             && s.Exchange == newExchange,
                        ct);
                if (duplicateListing)
                {
                    return new EditMutationResult(ActionResult: Conflict(new StockMutationBlockedResponse
                    {
                        Message = BuildListingDuplicateMessage(newTicker, newExchange),
                        Diagnostics = new StockDependencyDiagnosticsResponse
                        {
                            StockId = stock.Id,
                            HasBlockers = true,
                            Blockers =
                            [
                                new StockDependencyBlockerResponse
                                {
                                    Category = "duplicateListing",
                                    DisplayName = "Конфликт листинга",
                                    Count = 1,
                                    RelatedNames = [$"{newTicker} ({newExchange})"],
                                },
                            ],
                        },
                    }));
                }
            }

            if (request.RetainProviderSymbol && !string.IsNullOrWhiteSpace(stock.ProviderSymbol))
            {
                var providerSymbol = stock.ProviderSymbol.Trim();
                var providerConflict = await _context.Stocks
                    .AsNoTracking()
                    .AnyAsync(s => s.Id != stock.Id && s.ProviderSymbol == providerSymbol, ct);
                if (providerConflict)
                {
                    return new EditMutationResult(ActionResult: Conflict(new StockMutationBlockedResponse
                    {
                        Message = BuildProviderSymbolDuplicateMessage(providerSymbol),
                        Diagnostics = new StockDependencyDiagnosticsResponse
                        {
                            StockId = stock.Id,
                            HasBlockers = true,
                            Blockers =
                            [
                                new StockDependencyBlockerResponse
                                {
                                    Category = "duplicateProviderSymbol",
                                    DisplayName = "Конфликт ProviderSymbol",
                                    Count = 1,
                                    RelatedNames = [providerSymbol],
                                },
                            ],
                        },
                    }));
                }
            }

            if (identityChanged)
            {
                var recheckDiagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
                if (recheckDiagnostics.HasBlockers)
                {
                    return new EditMutationResult(ActionResult: Conflict(BuildBlockedResponse("Невозможно изменить тикер/биржу: обнаружены зависимости при повторной проверке.", recheckDiagnostics)));
                }
            }

            var name = (request.Name ?? string.Empty).Trim();
            var commonName = string.IsNullOrWhiteSpace(request.CommonName) ? name : request.CommonName.Trim();

            stock.Ticker = newTicker;
            stock.Exchange = newExchange;
            stock.Name = name;
            stock.CommonName = commonName;
            stock.Wkn = wkn;
            stock.Isin = isin;
            stock.FinanzenNetSlug = finanzenNetSlug;
            stock.IndustryId = industryId;
            stock.SectorId = sectorId;
            stock.UpdatedAt = DateTime.UtcNow;
            SyncMarketIndices(stock, request.MarketIndexIds, marketIndices);

            // Manual price edit: clear stale snapshot fields so the UI never shows outdated
            // change/timestamp alongside a manually entered price.
            stock.CurrentPrice = request.CurrentPrice;
            stock.CurrentPriceChange = null;
            stock.CurrentPriceChangePercent = null;
            stock.CurrentPriceAt = null;
            stock.CurrentPriceIsDelayed = false;
            stock.CurrentPriceDelayWarning = null;

            var clearedHistoryRows = 0;
            var clearedFundamentalsRows = 0;
            var clearedEnrichmentRows = 0;
            if (identityChanged)
            {
                stock.ProviderSymbol = request.RetainProviderSymbol ? stock.ProviderSymbol?.Trim() : null;
                stock.LastIncrementalHistoryRefreshSucceededAtUtc = null;
                stock.LastHistoryReconciliationSucceededAtUtc = null;
                stock.LastFullHistoryBackfillSucceededAtUtc = null;
                stock.NextIncrementalHistoryRefreshAtUtc = DateTime.UtcNow;
                stock.NextHistoryReconciliationAtUtc = DateTime.UtcNow;
                stock.NextFullHistoryBackfillAtUtc = DateTime.UtcNow;

                (clearedHistoryRows, clearedFundamentalsRows, clearedEnrichmentRows) = await ClearStockDerivedDataAsync(stock.Id, ct);
            }

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                return new EditMutationResult(ActionResult: Conflict("Нарушено ограничение уникальности. Проверьте тикер/биржу, WKN, ISIN и provider symbol."));
            }

            return new EditMutationResult(
                StockId: stock.Id,
                IdentityChanged: identityChanged,
                OldTicker: oldTicker,
                OldExchange: oldExchange,
                NewTicker: newTicker,
                NewExchange: newExchange,
                ClearedHistoryRows: clearedHistoryRows,
                ClearedFundamentalsRows: clearedFundamentalsRows,
                ClearedEnrichmentRows: clearedEnrichmentRows);
        }, cancellationToken);

        if (mutationResult.ActionResult is not null)
        {
            return mutationResult.ActionResult;
        }

        if (mutationResult.IdentityChanged && _stockMetadataEnrichmentService is not null)
        {
            try
            {
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                await _stockMetadataEnrichmentService.EnqueueSelectedAsync([mutationResult.StockId], userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue metadata enrichment after stock identity update. StockId={StockId}",
                    mutationResult.StockId);
            }
        }

        _logger.LogInformation(
            "Stock edit saved. StockId={StockId} IdentityChanged={IdentityChanged} Old={OldTicker}/{OldExchange} New={NewTicker}/{NewExchange} ClearedHistoryRows={ClearedHistoryRows} ClearedFundamentalsRows={ClearedFundamentalsRows} ClearedEnrichmentRows={ClearedEnrichmentRows}",
            mutationResult.StockId,
            mutationResult.IdentityChanged,
            mutationResult.OldTicker,
            mutationResult.OldExchange,
            mutationResult.NewTicker,
            mutationResult.NewExchange,
            mutationResult.ClearedHistoryRows,
            mutationResult.ClearedFundamentalsRows,
            mutationResult.ClearedEnrichmentRows);

        return NoContent();
    }

    [HttpGet("{id}/dependency-diagnostics")]
    public async Task<ActionResult<StockDependencyDiagnosticsResponse>> GetDependencyDiagnostics(int id, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Stocks.AsNoTracking().AnyAsync(s => s.Id == id, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        var diagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(id, cancellationToken);
        return Ok(diagnostics);
    }

    [HttpPut("{id}/identity")]
    public async Task<ActionResult<StockIdentityChangeResponse>> UpdateIdentity(
        int id,
        UpdateStockIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        var mutationResult = await ExecuteMutationWithExecutionStrategyAsync(async ct =>
        {
            var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (stock is null)
            {
                return new IdentityMutationResult(ActionResult: NotFound());
            }

            var newTicker = NormalizeTicker(request.Ticker);
            if (string.IsNullOrWhiteSpace(newTicker))
            {
                return new IdentityMutationResult(ActionResult: BadRequest("Тикер не может быть пустым."));
            }

            if (!StockExchanges.TryNormalize(request.Exchange, out var newExchange))
            {
                return new IdentityMutationResult(ActionResult: BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [nameof(request.Exchange)] = [$"Exchange must be one of: {string.Join(", ", StockExchanges.Supported)}."]
                })));
            }

            var oldTicker = stock.Ticker;
            var oldExchange = stock.Exchange;
            var identityChanged = !string.Equals(oldTicker, newTicker, StringComparison.Ordinal)
                                  || !string.Equals(oldExchange, newExchange, StringComparison.Ordinal);
            var transitionLabel = $"{oldTicker} ({oldExchange}) → {newTicker} ({newExchange})";

            if (!identityChanged)
            {
                return new IdentityMutationResult(ActionResult: Ok(new StockIdentityChangeResponse
                {
                    StockId = stock.Id,
                    OldTicker = oldTicker,
                    OldExchange = oldExchange,
                    NewTicker = oldTicker,
                    NewExchange = oldExchange,
                    IdentityChanged = false,
                    ClearedHistoryRows = 0,
                    ClearedFundamentalsSnapshot = false,
                    ClearedEnrichmentResultRows = 0,
                    RefreshScheduled = false,
                }));
            }

            if (!string.Equals(request.ConfirmationText?.Trim(), transitionLabel, StringComparison.OrdinalIgnoreCase))
            {
                return new IdentityMutationResult(ActionResult: BadRequest($"Подтверждение не пройдено. Введите точно: {transitionLabel}"));
            }

            var diagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
            if (diagnostics.HasBlockers)
            {
                return new IdentityMutationResult(ActionResult: Conflict(BuildBlockedResponse(
                    "Невозможно изменить тикер/биржу: обнаружены бизнес-зависимости. Исправление идентичности разрешено только для нереференсной акции.",
                    diagnostics)));
            }

            var duplicateListing = await _context.Stocks
                .AsNoTracking()
                .AnyAsync(
                    s => s.Id != stock.Id
                         && s.Ticker == newTicker
                         && s.Exchange == newExchange,
                    ct);
            if (duplicateListing)
            {
                return new IdentityMutationResult(ActionResult: Conflict(new StockMutationBlockedResponse
                {
                    Message = BuildListingDuplicateMessage(newTicker, newExchange),
                    Diagnostics = new StockDependencyDiagnosticsResponse
                    {
                        StockId = stock.Id,
                        HasBlockers = true,
                        Blockers =
                        [
                            new StockDependencyBlockerResponse
                            {
                                Category = "duplicateListing",
                                DisplayName = "Конфликт листинга",
                                Count = 1,
                                RelatedNames = [$"{newTicker} ({newExchange})"],
                            },
                        ],
                    },
                }));
            }

            if (request.RetainProviderSymbol && !string.IsNullOrWhiteSpace(stock.ProviderSymbol))
            {
                var providerSymbol = stock.ProviderSymbol.Trim();
                var providerConflict = await _context.Stocks
                    .AsNoTracking()
                    .AnyAsync(s => s.Id != stock.Id && s.ProviderSymbol == providerSymbol, ct);
                if (providerConflict)
                {
                    return new IdentityMutationResult(ActionResult: Conflict(new StockMutationBlockedResponse
                    {
                        Message = BuildProviderSymbolDuplicateMessage(providerSymbol),
                        Diagnostics = new StockDependencyDiagnosticsResponse
                        {
                            StockId = stock.Id,
                            HasBlockers = true,
                            Blockers =
                            [
                                new StockDependencyBlockerResponse
                                {
                                    Category = "duplicateProviderSymbol",
                                    DisplayName = "Конфликт ProviderSymbol",
                                    Count = 1,
                                    RelatedNames = [providerSymbol],
                                },
                            ],
                        },
                    }));
                }
            }

            var recheckDiagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
            if (recheckDiagnostics.HasBlockers)
            {
                return new IdentityMutationResult(ActionResult: Conflict(BuildBlockedResponse("Невозможно изменить тикер/биржу: обнаружены зависимости при повторной проверке.", recheckDiagnostics)));
            }

            var historyRows = await _context.StockHistoricalPrices.Where(x => x.StockId == stock.Id).ToListAsync(ct);
            var fundamentals = await _context.FundamentalsSnapshots.Where(x => x.StockId == stock.Id).ToListAsync(ct);
            var enrichmentResults = await _context.StockMetadataEnrichmentResults.Where(x => x.StockId == stock.Id).ToListAsync(ct);

            stock.Ticker = newTicker;
            stock.Exchange = newExchange;
            stock.ProviderSymbol = request.RetainProviderSymbol ? stock.ProviderSymbol?.Trim() : null;
            stock.FinanzenNetSlug = null;
            stock.CurrentPrice = 0m;
            stock.CurrentPriceChange = null;
            stock.CurrentPriceChangePercent = null;
            stock.CurrentPriceAt = null;
            stock.CurrentPriceIsDelayed = false;
            stock.CurrentPriceDelayWarning = null;
            stock.LastIncrementalHistoryRefreshSucceededAtUtc = null;
            stock.LastHistoryReconciliationSucceededAtUtc = null;
            stock.LastFullHistoryBackfillSucceededAtUtc = null;
            stock.NextIncrementalHistoryRefreshAtUtc = DateTime.UtcNow;
            stock.NextHistoryReconciliationAtUtc = DateTime.UtcNow;
            stock.NextFullHistoryBackfillAtUtc = DateTime.UtcNow;
            stock.UpdatedAt = DateTime.UtcNow;

            if (historyRows.Count > 0)
            {
                _context.StockHistoricalPrices.RemoveRange(historyRows);
            }

            if (fundamentals.Count > 0)
            {
                _context.FundamentalsSnapshots.RemoveRange(fundamentals);
            }

            if (enrichmentResults.Count > 0)
            {
                _context.StockMetadataEnrichmentResults.RemoveRange(enrichmentResults);
            }

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                return new IdentityMutationResult(ActionResult: Conflict("Нарушено ограничение уникальности при изменении тикера/биржи."));
            }

            return new IdentityMutationResult(
                StockId: stock.Id,
                OldTicker: oldTicker,
                OldExchange: oldExchange,
                NewTicker: newTicker,
                NewExchange: newExchange,
                ClearedHistoryRows: historyRows.Count,
                ClearedFundamentalsSnapshot: fundamentals.Count > 0,
                ClearedEnrichmentResultRows: enrichmentResults.Count,
                RefreshScheduled: true);
        }, cancellationToken);

        if (mutationResult.ActionResult is not null)
        {
            return mutationResult.ActionResult;
        }

        string? refreshWarning = null;
        if (_stockMetadataEnrichmentService is not null)
        {
            try
            {
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                await _stockMetadataEnrichmentService.EnqueueSelectedAsync([mutationResult.StockId], userId, cancellationToken);
            }
            catch (Exception ex)
            {
                refreshWarning = $"Не удалось запланировать enrichment: {ex.Message}";
            }
        }

        _logger.LogInformation(
            "Stock identity corrected. StockId={StockId} Old={OldTicker}/{OldExchange} New={NewTicker}/{NewExchange} ClearedHistoryRows={ClearedHistoryRows} ClearedFundamentals={ClearedFundamentals}",
            mutationResult.StockId,
            mutationResult.OldTicker,
            mutationResult.OldExchange,
            mutationResult.NewTicker,
            mutationResult.NewExchange,
            mutationResult.ClearedHistoryRows,
            mutationResult.ClearedFundamentalsSnapshot);

        return Ok(new StockIdentityChangeResponse
        {
            StockId = mutationResult.StockId,
            OldTicker = mutationResult.OldTicker,
            OldExchange = mutationResult.OldExchange,
            NewTicker = mutationResult.NewTicker,
            NewExchange = mutationResult.NewExchange,
            IdentityChanged = true,
            ClearedHistoryRows = mutationResult.ClearedHistoryRows,
            ClearedFundamentalsSnapshot = mutationResult.ClearedFundamentalsSnapshot,
            ClearedEnrichmentResultRows = mutationResult.ClearedEnrichmentResultRows,
            RefreshScheduled = mutationResult.RefreshScheduled,
            RefreshWarning = refreshWarning,
        });
    }

    [HttpDelete("{id}/permanent")]
    public async Task<IActionResult> DeletePermanent(
        int id,
        [FromBody] DeleteStockPermanentRequest? request,
        CancellationToken cancellationToken = default)
    {
        var mutationResult = await ExecuteMutationWithExecutionStrategyAsync(async ct =>
        {
            var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (stock is null)
            {
                return new DeletePermanentMutationResult(ActionResult: NotFound());
            }

            var diagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
            if (diagnostics.HasBlockers)
            {
                return new DeletePermanentMutationResult(ActionResult: Conflict(BuildBlockedResponse(
                    "Невозможно удалить акцию полностью: сначала устраните зависимости (портфели, транзакции, ордера, индексы).",
                    diagnostics)));
            }

            var recheckDiagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
            if (recheckDiagnostics.HasBlockers)
            {
                return new DeletePermanentMutationResult(ActionResult: Conflict(BuildBlockedResponse("Удаление отменено: при повторной проверке появились зависимости.", recheckDiagnostics)));
            }

            var (clearedHistoryRows, clearedFundamentalsRows, clearedEnrichmentRows) = await ClearStockDerivedDataAsync(stock.Id, ct);

            _context.Stocks.Remove(stock);
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                var latestDiagnostics = await _stockDependencyDiagnosticsService.GetDiagnosticsAsync(stock.Id, ct);
                if (latestDiagnostics.HasBlockers)
                {
                    _logger.LogWarning(
                        ex,
                        "Stock permanent delete blocked by dependencies after recheck. StockId={StockId} Ticker={Ticker} Exchange={Exchange}",
                        stock.Id,
                        stock.Ticker,
                        stock.Exchange);
                    return new DeletePermanentMutationResult(ActionResult: Conflict(BuildBlockedResponse("Удаление отменено: обнаружены зависимости при сохранении.", latestDiagnostics)));
                }

                var fkDetails = ExtractForeignKeyDetails(ex);
                _logger.LogError(
                    ex,
                    "Stock permanent delete failed with foreign key conflict. StockId={StockId} Ticker={Ticker} Exchange={Exchange} ForeignKey={ForeignKeyDetails}",
                    stock.Id,
                    stock.Ticker,
                    stock.Exchange,
                    fkDetails);

                var blockers = new List<StockDependencyBlockerResponse>
                {
                    new()
                    {
                        Category = "unexpectedForeignKeyConflict",
                        DisplayName = "Необработанная зависимость БД",
                        Count = 1,
                        RelatedNames = [fkDetails],
                    },
                };

                return new DeletePermanentMutationResult(ActionResult: Conflict(BuildBlockedResponse(
                    "Удаление заблокировано неучтённой зависимостью БД. Проверьте diagnostics и FK в логах.",
                    new StockDependencyDiagnosticsResponse
                    {
                        StockId = stock.Id,
                        HasBlockers = true,
                        Blockers = blockers,
                    })));
            }

            return new DeletePermanentMutationResult(
                StockId: stock.Id,
                Ticker: stock.Ticker,
                Exchange: stock.Exchange,
                ClearedHistoryRows: clearedHistoryRows,
                ClearedFundamentalsRows: clearedFundamentalsRows,
                ClearedEnrichmentRows: clearedEnrichmentRows);
        }, cancellationToken);

        if (mutationResult.ActionResult is not null)
        {
            return mutationResult.ActionResult;
        }

        _logger.LogInformation(
            "Stock permanently deleted. StockId={StockId} Ticker={Ticker} Exchange={Exchange} ClearedHistoryRows={ClearedHistoryRows} ClearedFundamentalsRows={ClearedFundamentalsRows} ClearedEnrichmentRows={ClearedEnrichmentRows}",
            mutationResult.StockId,
            mutationResult.Ticker,
            mutationResult.Exchange,
            mutationResult.ClearedHistoryRows,
            mutationResult.ClearedFundamentalsRows,
            mutationResult.ClearedEnrichmentRows);
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

    [HttpPut("{id}/purchase-candidate-priority")]
    public async Task<ActionResult<UpdateStockPurchaseCandidatePriorityResponse>> UpdatePurchaseCandidatePriority(
        int id,
        UpdateStockPurchaseCandidatePriorityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Priority))
        {
            return BadRequest("Недопустимое значение приоритета кандидата на покупку.");
        }

        var mutationResult = await ExecuteMutationWithExecutionStrategyAsync(async ct =>
        {
            var stock = await _context.Stocks.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (stock is null)
            {
                return new UpdatePurchaseCandidatePriorityMutationResult(ActionResult: NotFound());
            }

            if (stock.TrackingStatus != StockTrackingStatus.Tracked)
            {
                return new UpdatePurchaseCandidatePriorityMutationResult(
                    ActionResult: Conflict("Отметка кандидата доступна только для отслеживаемых акций."));
            }

            var belongsToPortfolio = await _context.PortfolioItems
                .AnyAsync(item => item.StockId == stock.Id, ct);

            if (belongsToPortfolio && request.Priority != StockPurchaseCandidatePriority.None)
            {
                return new UpdatePurchaseCandidatePriorityMutationResult(
                    ActionResult: Conflict("Нельзя установить отметку кандидата для акции, которая уже находится в портфеле."));
            }

            var authoritativePriority = belongsToPortfolio
                ? StockPurchaseCandidatePriority.None
                : request.Priority;

            if (stock.PurchaseCandidatePriority != authoritativePriority)
            {
                stock.PurchaseCandidatePriority = authoritativePriority;
                stock.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
            }

            return new UpdatePurchaseCandidatePriorityMutationResult(
                Response: new UpdateStockPurchaseCandidatePriorityResponse
                {
                    StockId = stock.Id,
                    Priority = stock.PurchaseCandidatePriority,
                });
        }, cancellationToken);

        if (mutationResult.ActionResult is not null)
        {
            return mutationResult.ActionResult;
        }

        return Ok(mutationResult.Response);
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

    private async Task<TMutationResult> ExecuteMutationWithExecutionStrategyAsync<TMutationResult>(
        Func<CancellationToken, Task<TMutationResult>> mutation,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;
            try
            {
                var result = await mutation(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
        });
    }

    private async Task<(int ClearedHistoryRows, int ClearedFundamentalsRows, int ClearedEnrichmentRows)> ClearStockDerivedDataAsync(
        int stockId,
        CancellationToken cancellationToken)
    {
        var historyRows = await _context.StockHistoricalPrices.Where(x => x.StockId == stockId).ToListAsync(cancellationToken);
        var fundamentals = await _context.FundamentalsSnapshots.Where(x => x.StockId == stockId).ToListAsync(cancellationToken);
        var enrichmentResults = await _context.StockMetadataEnrichmentResults.Where(x => x.StockId == stockId).ToListAsync(cancellationToken);

        if (historyRows.Count > 0)
        {
            _context.StockHistoricalPrices.RemoveRange(historyRows);
        }

        if (fundamentals.Count > 0)
        {
            _context.FundamentalsSnapshots.RemoveRange(fundamentals);
        }

        if (enrichmentResults.Count > 0)
        {
            _context.StockMetadataEnrichmentResults.RemoveRange(enrichmentResults);
        }

        return (historyRows.Count, fundamentals.Count, enrichmentResults.Count);
    }

    private static string ExtractForeignKeyDetails(DbUpdateException ex)
    {
        var details = ex.InnerException?.Message ?? ex.Message;
        if (string.IsNullOrWhiteSpace(details))
        {
            return "unknown-constraint";
        }

        return details.Length <= 600 ? details : details[..600];
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

    private static StockMutationBlockedResponse BuildBlockedResponse(string message, StockDependencyDiagnosticsResponse diagnostics)
        => new()
        {
            Message = message,
            Diagnostics = diagnostics,
        };

    private sealed record EditMutationResult(
        IActionResult? ActionResult = null,
        int StockId = 0,
        bool IdentityChanged = false,
        string OldTicker = "",
        string OldExchange = "",
        string NewTicker = "",
        string NewExchange = "",
        int ClearedHistoryRows = 0,
        int ClearedFundamentalsRows = 0,
        int ClearedEnrichmentRows = 0);

    private sealed record IdentityMutationResult(
        ActionResult<StockIdentityChangeResponse>? ActionResult = null,
        int StockId = 0,
        string OldTicker = "",
        string OldExchange = "",
        string NewTicker = "",
        string NewExchange = "",
        int ClearedHistoryRows = 0,
        bool ClearedFundamentalsSnapshot = false,
        int ClearedEnrichmentResultRows = 0,
        bool RefreshScheduled = false);

    private sealed record DeletePermanentMutationResult(
        IActionResult? ActionResult = null,
        int StockId = 0,
        string Ticker = "",
        string Exchange = "",
        int ClearedHistoryRows = 0,
        int ClearedFundamentalsRows = 0,
        int ClearedEnrichmentRows = 0);

    private sealed record UpdatePurchaseCandidatePriorityMutationResult(
        ActionResult<UpdateStockPurchaseCandidatePriorityResponse>? ActionResult = null,
        UpdateStockPurchaseCandidatePriorityResponse? Response = null);
}
