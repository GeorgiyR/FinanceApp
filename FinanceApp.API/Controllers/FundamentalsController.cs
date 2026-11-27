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
[Route("api/Stocks/{stockId}/fundamentals")]
[Authorize]
public class FundamentalsController : ControllerBase
{
    private readonly IFundamentalsService _fundamentalsService;
    private readonly AppDbContext _context;

    public FundamentalsController(IFundamentalsService fundamentalsService, AppDbContext context)
    {
        _fundamentalsService = fundamentalsService;
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<FundamentalsResponse>> Get(int stockId, CancellationToken ct)
    {
        try
        {
            var result = await _fundamentalsService.GetFundamentalsAsync(stockId, ct);
            return Ok(ToResponse(stockId, result));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<FundamentalsResponse>> Refresh(int stockId, CancellationToken ct)
    {
        var stock = await _context.Stocks.FindAsync([stockId], ct);
        if (stock is null) return NotFound();

        if (stock.TrackingStatus == StockTrackingStatus.CatalogOnly)
        {
            return StatusCode(StatusCodes.Status409Conflict,
                "Обновление фундаментальных данных недоступно для каталожных акций. Добавьте акцию в отслеживаемые.");
        }

        try
        {
            var result = await _fundamentalsService.RefreshFundamentalsAsync(stockId, ct);
            return Ok(ToResponse(stockId, result));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static FundamentalsResponse ToResponse(int stockId, FundamentalsResult result)
    {
        var snapshot = result.Snapshot;
        return new FundamentalsResponse
        {
            StockId = stockId,
            State = result.State.ToString(),
            WarningMessage = result.WarningMessage,
            Snapshot = snapshot is null
                ? null
                : new FundamentalsSnapshotDto
                {
                    Id = snapshot.Id,
                    SourceSymbol = snapshot.SourceSymbol,
                    MarketCap = snapshot.MarketCap,
                    EnterpriseValue = snapshot.EnterpriseValue,
                    TotalDebt = snapshot.TotalDebt,
                    CashAndEquivalents = snapshot.CashAndEquivalents,
                    RevenueTtm = snapshot.RevenueTtm,
                    NetIncomeTtm = snapshot.NetIncomeTtm,
                    EbitdaTtm = snapshot.EbitdaTtm,
                    OperatingIncomeTtm = snapshot.OperatingIncomeTtm,
                    FreeCashFlowTtm = snapshot.FreeCashFlowTtm,
                    TotalAssets = snapshot.TotalAssets,
                    TotalLiabilities = snapshot.TotalLiabilities,
                    PeRatio = snapshot.PeRatio,
                    ForwardPeRatio = snapshot.ForwardPeRatio,
                    PbRatio = snapshot.PbRatio,
                    DividendYield = snapshot.DividendYield,
                    Currency = snapshot.Currency,
                    Source = snapshot.Source,
                    AsOfDate = snapshot.AsOfDate,
                    FetchedAtUtc = snapshot.FetchedAtUtc
                },
            Periods = snapshot is null
                ? Array.Empty<FinancialPeriodDto>()
                : snapshot.Periods
                    .OrderByDescending(x => x.PeriodEndDate)
                    .ThenByDescending(x => x.PeriodType)
                    .Select(x => new FinancialPeriodDto
                    {
                        Id = x.Id,
                        PeriodType = x.PeriodType.ToString(),
                        FiscalYear = x.FiscalYear,
                        FiscalQuarter = x.FiscalQuarter,
                        PeriodEndDate = x.PeriodEndDate,
                        ReportedCurrency = x.ReportedCurrency,
                        Revenue = x.Revenue,
                        OperatingIncome = x.OperatingIncome,
                        NetIncome = x.NetIncome,
                        EpsReported = x.EpsReported,
                        EpsEstimate = x.EpsEstimate,
                        Ebitda = x.Ebitda,
                        TotalDebt = x.TotalDebt,
                        TotalAssets = x.TotalAssets,
                        TotalLiabilities = x.TotalLiabilities,
                        FreeCashFlow = x.FreeCashFlow,
                        Source = x.Source,
                        AsOfDate = x.AsOfDate,
                        FetchedAtUtc = x.FetchedAtUtc
                    })
                    .ToList(),
            EarningsEvents = snapshot is null
                ? Array.Empty<EarningsEventDto>()
                : snapshot.EarningsEvents
                    .OrderBy(x => x.ReportDate ?? x.FiscalPeriodEnd ?? DateTime.MaxValue)
                    .ThenBy(x => x.FiscalPeriod)
                    .Select(x => new EarningsEventDto
                    {
                        Id = x.Id,
                        ReportDate = x.ReportDate,
                        FiscalPeriodEnd = x.FiscalPeriodEnd,
                        ReportDateEnd = x.ReportDateEnd,
                        DateStatus = x.DateStatus.ToString(),
                        EpsEstimate = x.EpsEstimate,
                        EpsReported = x.EpsReported,
                        RevenueEstimate = x.RevenueEstimate,
                        RevenueReported = x.RevenueReported,
                        FiscalPeriod = x.FiscalPeriod,
                        Source = x.Source,
                        FetchedAtUtc = x.FetchedAtUtc
                    })
                    .ToList()
        };
    }
}
