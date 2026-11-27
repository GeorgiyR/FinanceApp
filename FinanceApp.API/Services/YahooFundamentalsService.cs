using System.Globalization;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using FinanceApp.Core.Models;
using Microsoft.AspNetCore.Http;

namespace FinanceApp.API.Services;

public interface IYahooFundamentalsService
{
    Task<YahooFundamentalsResult> GetFundamentalsAsync(string symbol, CancellationToken cancellationToken = default);
}

public sealed record YahooFundamentalsResult(
    CompanyFundamentalsSnapshot? Snapshot,
    int StatusCode,
    string? ErrorMessage,
    YahooFundamentalsFailureCategory FailureCategory = YahooFundamentalsFailureCategory.None)
{
    public bool IsSuccess => Snapshot is not null;

    public static YahooFundamentalsResult Success(CompanyFundamentalsSnapshot snapshot) =>
        new(snapshot, StatusCodes.Status200OK, null, YahooFundamentalsFailureCategory.None);

    public static YahooFundamentalsResult Failure(
        int statusCode,
        string errorMessage,
        YahooFundamentalsFailureCategory failureCategory = YahooFundamentalsFailureCategory.ProviderRequestFailed) =>
        new(null, statusCode, errorMessage, failureCategory);
}

public enum YahooFundamentalsFailureCategory
{
    None = 0,
    ProviderUnauthorized,
    ProviderForbidden,
    ProviderNotFound,
    ProviderRateLimited,
    ProviderServerError,
    ProviderRequestFailed,
    ProviderTimeout,
    InvalidProviderResponse,
    ProviderConsentFailure,
    SessionInitializationFailed
}

public sealed class YahooFundamentalsService : IYahooFundamentalsService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(20);
    private const string ProviderName = "Yahoo Finance";
    private const string Modules =
        "summaryDetail,financialData,defaultKeyStatistics,incomeStatementHistory,incomeStatementHistoryQuarterly,earningsHistory,earningsTrend,cashflowStatement,cashflowStatementQuarterly,cashflowStatementHistory,cashflowStatementHistoryQuarterly,balanceSheetHistory,balanceSheetHistoryQuarterly,calendarEvents";

    private readonly IYahooRequestCoordinator _yahooRequestCoordinator;
    private readonly IYahooSessionService _yahooSessionService;
    private readonly ILogger<YahooFundamentalsService> _logger;
    private readonly TimeProvider _timeProvider;

    public YahooFundamentalsService(
        IYahooRequestCoordinator yahooRequestCoordinator,
        IYahooSessionService yahooSessionService,
        ILogger<YahooFundamentalsService> logger,
        TimeProvider? timeProvider = null)
    {
        _yahooRequestCoordinator = yahooRequestCoordinator;
        _yahooSessionService = yahooSessionService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<YahooFundamentalsResult> GetFundamentalsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var safeSymbol = SanitizeForLog(symbol);
        var requestLabel = $"fundamentals:{safeSymbol}";
        var startedAt = _timeProvider.GetTimestamp();

        try
        {
            var initialSession = await _yahooSessionService.GetSessionAsync(cancellationToken);
            if (!initialSession.IsSuccess || initialSession.Session is null)
            {
                _logger.LogWarning(
                    "Yahoo fundamentals session initialization failed for {Symbol}; category={Category} status={StatusCode}.",
                    safeSymbol,
                    initialSession.FailureCategory,
                    initialSession.StatusCode);
                return MapSessionFailure(initialSession);
            }

            var response = await SendQuoteSummaryAsync(symbol, requestLabel, initialSession.Session, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && IsUnauthorizedOrInvalidCrumb(response.Content))
            {
                await _yahooSessionService.InvalidateSessionAsync(cancellationToken);
                var refreshedSession = await _yahooSessionService.GetSessionAsync(cancellationToken);
                if (!refreshedSession.IsSuccess || refreshedSession.Session is null)
                {
                    return MapSessionFailure(refreshedSession);
                }

                response = await SendQuoteSummaryAsync(symbol, requestLabel, refreshedSession.Session, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && IsUnauthorizedOrInvalidCrumb(response.Content))
                {
                    _logger.LogWarning(
                        "Yahoo fundamentals request failed for {Symbol}; status={StatusCode}; sessionRefresh=failed-second-401.",
                        safeSymbol,
                        (int)response.StatusCode);
                    return YahooFundamentalsResult.Failure(
                        StatusCodes.Status502BadGateway,
                        "Fundamentals provider authorization failed.",
                        YahooFundamentalsFailureCategory.ProviderUnauthorized);
                }
            }

            var parsedError = TryParseYahooErrorEnvelope(response.Content);
            if (response.IsRateLimited)
            {
                _logger.LogWarning(
                    "Yahoo fundamentals request rate limit exceeded for {Symbol}; cooldownUntilUtc={CooldownUntilUtc}.",
                    safeSymbol,
                    response.CooldownUntilUtc);
                return YahooFundamentalsResult.Failure(
                    StatusCodes.Status429TooManyRequests,
                    "Fundamentals provider rate limit exceeded.",
                    YahooFundamentalsFailureCategory.ProviderRateLimited);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Yahoo fundamentals request failed for {Symbol}: status={StatusCode} providerCode={ProviderCode}.",
                    safeSymbol,
                    (int)response.StatusCode,
                    parsedError?.Code);
                return MapFailureResponse(response.StatusCode);
            }

            if (string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("Yahoo fundamentals returned empty response for {Symbol}.", safeSymbol);
                return YahooFundamentalsResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "Fundamentals provider returned an invalid response.",
                    YahooFundamentalsFailureCategory.InvalidProviderResponse);
            }

            return ParseFundamentals(symbol, response.Content, _timeProvider);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Yahoo fundamentals request timed out for {Symbol}; exceptionType={ExceptionType}.",
                safeSymbol,
                ex.GetType().Name);
            return YahooFundamentalsResult.Failure(
                StatusCodes.Status504GatewayTimeout,
                "Fundamentals provider request timed out.",
                YahooFundamentalsFailureCategory.ProviderTimeout);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Yahoo fundamentals request failed for {Symbol}; httpRequestError={RequestError}.",
                safeSymbol,
                ex.HttpRequestError);
            return YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider request failed.",
                YahooFundamentalsFailureCategory.ProviderRequestFailed);
        }
        finally
        {
            var elapsedMs = _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogInformation("Yahoo fundamentals finished for {Symbol}; durationMs={DurationMs}.", safeSymbol, (int)elapsedMs);
        }
    }

    private async Task<YahooHttpResponse> SendQuoteSummaryAsync(
        string symbol,
        string requestLabel,
        YahooSession session,
        CancellationToken cancellationToken)
    {
        var url = BuildQuoteSummaryUrl(symbol, session.Crumb);
        return await _yahooRequestCoordinator.GetAsync(
            url,
            requestLabel,
            new YahooRequestExecutionOptions(
                MaxAttempts,
                RetryBaseDelay,
                RetryMaxDelay,
                null,
                ContainsSensitiveQueryParameters: true),
            cancellationToken,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = session.CookieHeader
            });
    }

    private static string BuildQuoteSummaryUrl(string symbol, string crumb) =>
        $"https://query2.finance.yahoo.com/v10/finance/quoteSummary/{Uri.EscapeDataString(symbol)}?modules={Uri.EscapeDataString(Modules)}&crumb={Uri.EscapeDataString(crumb)}";

    private static bool IsUnauthorizedOrInvalidCrumb(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var parsed = TryParseYahooErrorEnvelope(payload);
        if (parsed?.Code?.Equals("Unauthorized", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return payload.IndexOf("Invalid Crumb", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static YahooProviderError? TryParseYahooErrorEnvelope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("finance", out var finance) &&
                finance.TryGetProperty("error", out var error))
            {
                return new YahooProviderError(
                    error.TryGetProperty("code", out var code) ? code.GetString() : null,
                    error.TryGetProperty("description", out var description) ? description.GetString() : null);
            }
        }
        catch (JsonException)
        {
            // ignored
        }

        return null;
    }

    private static YahooFundamentalsResult MapSessionFailure(YahooSessionAcquisitionResult sessionResult) =>
        sessionResult.FailureCategory switch
        {
            YahooSessionFailureCategory.Timeout => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderTimeout),
            YahooSessionFailureCategory.RateLimited => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderRateLimited),
            YahooSessionFailureCategory.Unauthorized => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderUnauthorized),
            YahooSessionFailureCategory.Forbidden => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderForbidden),
            YahooSessionFailureCategory.NotFound => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderNotFound),
            YahooSessionFailureCategory.ConsentFailure => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.ProviderConsentFailure),
            _ => YahooFundamentalsResult.Failure(
                sessionResult.StatusCode,
                sessionResult.ErrorMessage,
                YahooFundamentalsFailureCategory.SessionInitializationFailed)
        };

    private static YahooFundamentalsResult MapFailureResponse(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized => YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider authorization failed.",
                YahooFundamentalsFailureCategory.ProviderUnauthorized),
            HttpStatusCode.Forbidden => YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider access is forbidden.",
                YahooFundamentalsFailureCategory.ProviderForbidden),
            HttpStatusCode.NotFound => YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider endpoint not found.",
                YahooFundamentalsFailureCategory.ProviderNotFound),
            HttpStatusCode.TooManyRequests => YahooFundamentalsResult.Failure(
                StatusCodes.Status429TooManyRequests,
                "Fundamentals provider rate limit exceeded.",
                YahooFundamentalsFailureCategory.ProviderRateLimited),
            var status when (int)status >= 500 => YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider request failed.",
                YahooFundamentalsFailureCategory.ProviderServerError),
            _ => YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider request failed.",
                YahooFundamentalsFailureCategory.ProviderRequestFailed)
        };

    private static YahooFundamentalsResult ParseFundamentals(string symbol, string payload, TimeProvider timeProvider)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!TryGetQuoteSummaryResult(document.RootElement, out var result))
            {
                return YahooFundamentalsResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "Fundamentals provider returned an invalid response.",
                    YahooFundamentalsFailureCategory.InvalidProviderResponse);
            }

            var fetchedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var periods = BuildPeriods(result, fetchedAtUtc);
            var earningsEvents = BuildEarningsEvents(result, fetchedAtUtc);

            var summaryDetail = GetModule(result, "summaryDetail");
            var financialData = GetModule(result, "financialData");
            var defaultKeyStatistics = GetModule(result, "defaultKeyStatistics");

            var quarterlyPeriods = periods
                .Where(x => x.PeriodType == PeriodType.Quarterly)
                .OrderByDescending(x => x.PeriodEndDate)
                .ToList();
            var annualPeriods = periods
                .Where(x => x.PeriodType == PeriodType.Annual)
                .OrderByDescending(x => x.PeriodEndDate)
                .ToList();
            var latestBalancePeriod = quarterlyPeriods
                .Concat(annualPeriods)
                .FirstOrDefault(x => x.TotalAssets.HasValue || x.TotalLiabilities.HasValue);

            var snapshot = new CompanyFundamentalsSnapshot
            {
                SourceSymbol = symbol,
                MarketCap = GetRawDecimal(summaryDetail, "marketCap"),
                EnterpriseValue = GetRawDecimal(defaultKeyStatistics, "enterpriseValue") ?? GetRawDecimal(summaryDetail, "enterpriseValue"),
                TotalDebt = GetRawDecimal(financialData, "totalDebt"),
                CashAndEquivalents = GetRawDecimal(financialData, "totalCash"),
                RevenueTtm = SumTtm(quarterlyPeriods, x => x.Revenue) ?? annualPeriods.FirstOrDefault()?.Revenue,
                NetIncomeTtm = SumTtm(quarterlyPeriods, x => x.NetIncome) ?? annualPeriods.FirstOrDefault()?.NetIncome,
                EbitdaTtm = SumTtm(quarterlyPeriods, x => x.Ebitda) ?? annualPeriods.FirstOrDefault()?.Ebitda ?? GetRawDecimal(financialData, "ebitda"),
                OperatingIncomeTtm = SumTtm(quarterlyPeriods, x => x.OperatingIncome) ?? annualPeriods.FirstOrDefault()?.OperatingIncome,
                FreeCashFlowTtm = SumTtm(quarterlyPeriods, x => x.FreeCashFlow) ?? annualPeriods.FirstOrDefault()?.FreeCashFlow ?? GetRawDecimal(financialData, "freeCashflow"),
                TotalAssets = latestBalancePeriod?.TotalAssets,
                TotalLiabilities = latestBalancePeriod?.TotalLiabilities,
                PeRatio = GetRawDecimal(summaryDetail, "trailingPE"),
                ForwardPeRatio = GetRawDecimal(summaryDetail, "forwardPE"),
                PbRatio = GetRawDecimal(summaryDetail, "priceToBook") ?? GetRawDecimal(defaultKeyStatistics, "priceToBook"),
                DividendYield = GetRawDecimal(summaryDetail, "dividendYield"),
                Currency = GetOptionalString(summaryDetail, "currency")
                    ?? GetOptionalString(financialData, "financialCurrency")
                    ?? GetOptionalString(defaultKeyStatistics, "financialCurrency"),
                Source = ProviderName,
                AsOfDate = ResolveAsOfDate(periods, earningsEvents),
                FetchedAtUtc = fetchedAtUtc,
                Periods = periods,
                EarningsEvents = earningsEvents
            };

            return YahooFundamentalsResult.Success(snapshot);
        }
        catch (JsonException)
        {
            return YahooFundamentalsResult.Failure(
                StatusCodes.Status502BadGateway,
                "Fundamentals provider returned an invalid response.",
                YahooFundamentalsFailureCategory.InvalidProviderResponse);
        }
    }

    private static bool TryGetQuoteSummaryResult(JsonElement root, out JsonElement result)
    {
        result = default;
        if (!root.TryGetProperty("quoteSummary", out var quoteSummary) ||
            !quoteSummary.TryGetProperty("result", out var resultArray) ||
            resultArray.ValueKind != JsonValueKind.Array ||
            resultArray.GetArrayLength() == 0)
        {
            return false;
        }

        result = resultArray[0];
        return true;
    }

    private static List<FinancialPeriod> BuildPeriods(JsonElement result, DateTime fetchedAtUtc)
    {
        var periods = new Dictionary<string, FinancialPeriod>(StringComparer.Ordinal);

        MergeIncomeStatements(periods, GetStatementArray(result, "incomeStatementHistory", "incomeStatementHistory"), PeriodType.Annual, fetchedAtUtc);
        MergeIncomeStatements(periods, GetStatementArray(result, "incomeStatementHistoryQuarterly", "incomeStatementHistory"), PeriodType.Quarterly, fetchedAtUtc);
        MergeBalanceSheets(periods, GetStatementArray(result, "balanceSheetHistory", "balanceSheetStatements"), PeriodType.Annual, fetchedAtUtc);
        MergeBalanceSheets(periods, GetStatementArray(result, "balanceSheetHistoryQuarterly", "balanceSheetStatements"), PeriodType.Quarterly, fetchedAtUtc);
        MergeCashflows(periods, GetStatementArray(result, "cashflowStatement", "cashflowStatements"), PeriodType.Annual, fetchedAtUtc);
        MergeCashflows(periods, GetStatementArray(result, "cashflowStatementQuarterly", "cashflowStatements"), PeriodType.Quarterly, fetchedAtUtc);
        MergeCashflows(periods, GetStatementArray(result, "cashflowStatementHistory", "cashflowStatements"), PeriodType.Annual, fetchedAtUtc);
        MergeCashflows(periods, GetStatementArray(result, "cashflowStatementHistoryQuarterly", "cashflowStatements"), PeriodType.Quarterly, fetchedAtUtc);

        return periods.Values
            .Where(x => x.PeriodEndDate.HasValue)
            .OrderByDescending(x => x.PeriodEndDate)
            .ThenByDescending(x => x.PeriodType)
            .ToList();
    }

    private static void MergeIncomeStatements(
        IDictionary<string, FinancialPeriod> periods,
        JsonElement? statements,
        PeriodType periodType,
        DateTime fetchedAtUtc)
    {
        if (statements is not { ValueKind: JsonValueKind.Array })
        {
            return;
        }

        foreach (var statement in statements.Value.EnumerateArray())
        {
            var period = GetOrCreatePeriod(periods, statement, periodType, fetchedAtUtc);
            if (period is null)
            {
                continue;
            }

            period.ReportedCurrency ??= GetOptionalString(statement, "currencyCode") ?? GetOptionalString(statement, "reportedCurrency");
            period.Revenue = GetRawDecimal(statement, "totalRevenue") ?? period.Revenue;
            period.OperatingIncome = GetRawDecimal(statement, "operatingIncome") ?? period.OperatingIncome;
            period.NetIncome = GetRawDecimal(statement, "netIncome") ?? period.NetIncome;
            period.Ebitda = GetRawDecimal(statement, "ebitda") ?? period.Ebitda;
        }
    }

    private static void MergeBalanceSheets(
        IDictionary<string, FinancialPeriod> periods,
        JsonElement? statements,
        PeriodType periodType,
        DateTime fetchedAtUtc)
    {
        if (statements is not { ValueKind: JsonValueKind.Array })
        {
            return;
        }

        foreach (var statement in statements.Value.EnumerateArray())
        {
            var period = GetOrCreatePeriod(periods, statement, periodType, fetchedAtUtc);
            if (period is null)
            {
                continue;
            }

            period.ReportedCurrency ??= GetOptionalString(statement, "currencyCode") ?? GetOptionalString(statement, "reportedCurrency");
            period.TotalDebt = GetRawDecimal(statement, "totalDebt") ?? period.TotalDebt;
            period.TotalAssets = GetRawDecimal(statement, "totalAssets") ?? period.TotalAssets;
            period.TotalLiabilities = GetRawDecimal(statement, "totalLiab")
                ?? GetRawDecimal(statement, "totalLiabilities")
                ?? period.TotalLiabilities;
        }
    }

    private static void MergeCashflows(
        IDictionary<string, FinancialPeriod> periods,
        JsonElement? statements,
        PeriodType periodType,
        DateTime fetchedAtUtc)
    {
        if (statements is not { ValueKind: JsonValueKind.Array })
        {
            return;
        }

        foreach (var statement in statements.Value.EnumerateArray())
        {
            var period = GetOrCreatePeriod(periods, statement, periodType, fetchedAtUtc);
            if (period is null)
            {
                continue;
            }

            period.ReportedCurrency ??= GetOptionalString(statement, "currencyCode") ?? GetOptionalString(statement, "reportedCurrency");
            var freeCashFlow = GetRawDecimal(statement, "freeCashFlow");
            if (!freeCashFlow.HasValue)
            {
                var operatingCashflow = GetRawDecimal(statement, "totalCashFromOperatingActivities");
                var capitalExpenditures = GetRawDecimal(statement, "capitalExpenditures");
                if (operatingCashflow.HasValue || capitalExpenditures.HasValue)
                {
                    freeCashFlow = (operatingCashflow ?? 0m) + (capitalExpenditures ?? 0m);
                }
            }

            period.FreeCashFlow = freeCashFlow ?? period.FreeCashFlow;
        }
    }

    private static FinancialPeriod? GetOrCreatePeriod(
        IDictionary<string, FinancialPeriod> periods,
        JsonElement statement,
        PeriodType periodType,
        DateTime fetchedAtUtc)
    {
        var periodEndDate = GetRawDateTime(statement, "endDate");
        if (!periodEndDate.HasValue)
        {
            return null;
        }

        var key = CreatePeriodKey(periodType, periodEndDate.Value);
        if (periods.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var period = new FinancialPeriod
        {
            PeriodType = periodType,
            PeriodEndDate = periodEndDate.Value.Date,
            FiscalYear = periodEndDate.Value.Year,
            FiscalQuarter = GetQuarter(periodEndDate.Value),
            Source = ProviderName,
            AsOfDate = periodEndDate.Value.Date,
            FetchedAtUtc = fetchedAtUtc
        };

        periods[key] = period;
        return period;
    }

    private static List<EarningsEvent> BuildEarningsEvents(JsonElement result, DateTime fetchedAtUtc)
    {
        var earnings = new Dictionary<string, EarningsEvent>(StringComparer.Ordinal);

        var earningsHistory = GetStatementArray(result, "earningsHistory", "history");
        if (earningsHistory is { ValueKind: JsonValueKind.Array })
        {
            foreach (var entry in earningsHistory.Value.EnumerateArray())
            {
                var periodEnd = GetRawDateTime(entry, "quarter")?.Date;
                var fiscalPeriod = GetOptionalString(entry, "quarter", "fmt");
                var key = CreateEarningsKey(periodEnd, fiscalPeriod);
                earnings[key] = new EarningsEvent
                {
                    FiscalPeriodEnd = periodEnd,
                    ReportDate = null,
                    DateStatus = GetRawDecimal(entry, "epsActual").HasValue ? EarningsDateStatus.Confirmed : EarningsDateStatus.Unknown,
                    EpsEstimate = GetRawDecimal(entry, "epsEstimate"),
                    EpsReported = GetRawDecimal(entry, "epsActual"),
                    RevenueEstimate = GetRawDecimal(entry, "revenueEstimate"),
                    RevenueReported = GetRawDecimal(entry, "revenueActual"),
                    FiscalPeriod = fiscalPeriod,
                    Source = ProviderName,
                    FetchedAtUtc = fetchedAtUtc
                };
            }
        }

        var earningsTrend = GetStatementArray(result, "earningsTrend", "trend");
        if (earningsTrend is { ValueKind: JsonValueKind.Array })
        {
            foreach (var entry in earningsTrend.Value.EnumerateArray())
            {
                var fiscalPeriod = GetOptionalString(entry, "period");
                if (string.IsNullOrWhiteSpace(fiscalPeriod))
                {
                    continue;
                }

                var periodEnd = GetRawDateTime(entry, "endDate")?.Date;
                var key = CreateEarningsKey(periodEnd, fiscalPeriod);
                if (!earnings.TryGetValue(key, out var existing))
                {
                    existing = new EarningsEvent
                    {
                        FiscalPeriodEnd = periodEnd,
                        ReportDate = null,
                        FiscalPeriod = fiscalPeriod,
                        Source = ProviderName,
                        FetchedAtUtc = fetchedAtUtc
                    };
                    earnings[key] = existing;
                }

                existing.DateStatus = existing.DateStatus == EarningsDateStatus.Confirmed
                    ? EarningsDateStatus.Confirmed
                    : EarningsDateStatus.Estimated;
                existing.EpsEstimate ??= GetRawDecimal(entry, "earningsEstimate", "avg");
                existing.RevenueEstimate ??= GetRawDecimal(entry, "revenueEstimate", "avg");
            }
        }

        var calendarEventDates = GetStatementArray(result, "calendarEvents", "earnings", "earningsDate");
        if (calendarEventDates is { ValueKind: JsonValueKind.Array } && calendarEventDates.Value.GetArrayLength() > 0)
        {
            var dates = calendarEventDates.Value
                .EnumerateArray()
                .Select(x => GetDateFromElement(x)?.Date)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .OrderBy(x => x)
                .ToList();

            if (dates.Count > 0)
            {
                var reportDate = dates[0];

                // Yahoo reports the fiscal period end in history/trend and the actual release
                // date in calendarEvents. Attach the release date to the most recent period
                // that ends before it instead of creating a second, unrelated row.
                var existing = earnings.Values
                    .Where(x => x.FiscalPeriodEnd is DateTime periodEnd && periodEnd <= reportDate)
                    .OrderByDescending(x => x.FiscalPeriodEnd)
                    .FirstOrDefault();

                if (existing is null)
                {
                    var key = CreateEarningsKey(reportDate, null);
                    if (!earnings.TryGetValue(key, out existing))
                    {
                        existing = new EarningsEvent
                        {
                            ReportDate = reportDate,
                            Source = ProviderName,
                            FetchedAtUtc = fetchedAtUtc
                        };
                        earnings[key] = existing;
                    }
                }

                existing.ReportDate = reportDate;
                existing.ReportDateEnd = dates[^1];
                if (existing.DateStatus != EarningsDateStatus.Confirmed)
                {
                    existing.DateStatus = EarningsDateStatus.Estimated;
                }
            }
        }

        return earnings.Values
            .OrderBy(x => x.ReportDate ?? x.FiscalPeriodEnd ?? DateTime.MaxValue)
            .ThenBy(x => x.FiscalPeriod)
            .ToList();
    }

    private static JsonElement? GetModule(JsonElement result, string moduleName)
    {
        return result.TryGetProperty(moduleName, out var module) ? module : null;
    }

    private static JsonElement? GetStatementArray(JsonElement result, string moduleName, params string[] propertyPath)
    {
        var current = GetModule(result, moduleName);
        if (current is null)
        {
            return null;
        }

        foreach (var path in propertyPath)
        {
            if (!current.Value.TryGetProperty(path, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static decimal? GetRawDecimal(JsonElement? element, params string[] propertyPath)
    {
        if (element is null)
        {
            return null;
        }

        var current = element.Value;
        foreach (var propertyName in propertyPath)
        {
            if (!current.TryGetProperty(propertyName, out current))
            {
                return null;
            }
        }

        return GetDecimalFromElement(current);
    }

    private static decimal? GetDecimalFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("raw", out var raw))
        {
            return GetDecimalFromElement(raw);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTime? GetRawDateTime(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        foreach (var propertyName in propertyPath)
        {
            if (!current.TryGetProperty(propertyName, out current))
            {
                return null;
            }
        }

        return GetDateFromElement(current);
    }

    private static DateTime? GetDateFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("raw", out var raw))
        {
            return GetDateFromElement(raw);
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var unixSeconds) && unixSeconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        if (element.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement? element, params string[] propertyPath)
    {
        if (element is null)
        {
            return null;
        }

        var current = element.Value;
        foreach (var propertyName in propertyPath)
        {
            if (!current.TryGetProperty(propertyName, out current))
            {
                return null;
            }
        }

        if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("fmt", out var fmt) && fmt.ValueKind == JsonValueKind.String)
        {
            return fmt.GetString();
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static decimal? SumTtm(
        IEnumerable<FinancialPeriod> quarterlyPeriods,
        Func<FinancialPeriod, decimal?> selector)
    {
        var latestFour = quarterlyPeriods
            .Where(x => x.PeriodEndDate.HasValue)
            .OrderByDescending(x => x.PeriodEndDate)
            .Take(4)
            .Select(selector)
            .ToList();

        if (latestFour.Count == 0 || latestFour.All(x => !x.HasValue))
        {
            return null;
        }

        return latestFour.Where(x => x.HasValue).Sum(x => x!.Value);
    }

    private static DateTime? ResolveAsOfDate(
        IEnumerable<FinancialPeriod> periods,
        IEnumerable<EarningsEvent> earningsEvents)
    {
        var periodDate = periods
            .Where(x => x.PeriodEndDate.HasValue)
            .MaxBy(x => x.PeriodEndDate)?
            .PeriodEndDate;

        var earningsDate = earningsEvents
            .Select(x => x.ReportDate ?? x.FiscalPeriodEnd)
            .Where(x => x.HasValue)
            .Max();

        if (periodDate is null)
        {
            return earningsDate;
        }

        if (earningsDate is null)
        {
            return periodDate;
        }

        return periodDate >= earningsDate ? periodDate : earningsDate;
    }

    private static int GetQuarter(DateTime date) => ((date.Month - 1) / 3) + 1;

    private static string CreatePeriodKey(PeriodType periodType, DateTime periodEndDate) =>
        $"{periodType}:{periodEndDate:yyyyMMdd}";

    private static string CreateEarningsKey(DateTime? reportDate, string? fiscalPeriod) =>
        $"{reportDate:yyyyMMdd}:{fiscalPeriod ?? string.Empty}";

    private static string SanitizeForLog(string value) =>
        value.Replace('\r', '_').Replace('\n', '_');

    private sealed record YahooProviderError(string? Code, string? Description);
}
