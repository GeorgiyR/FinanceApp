using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StockPriceController : ControllerBase
{
    private readonly IFinnhubQuoteService _finnhubQuoteService;
    private readonly IYahooQuoteService _yahooQuoteService;
    private readonly IFinanzenNetQuoteService _finanzenNetQuoteService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IStockQuoteConversionService _stockQuoteConversionService;

    public StockPriceController(
        IFinnhubQuoteService finnhubQuoteService,
        IYahooQuoteService yahooQuoteService,
        IFinanzenNetQuoteService finanzenNetQuoteService,
        IExchangeRateService exchangeRateService,
        IStockQuoteConversionService stockQuoteConversionService)
    {
        _finnhubQuoteService = finnhubQuoteService;
        _yahooQuoteService = yahooQuoteService;
        _finanzenNetQuoteService = finanzenNetQuoteService;
        _exchangeRateService = exchangeRateService;
        _stockQuoteConversionService = stockQuoteConversionService;
    }

    [HttpGet("rate/eurusd")]
    public async Task<IActionResult> GetEurUsdRate(CancellationToken cancellationToken = default)
    {
        var rate = await _exchangeRateService.GetRateToEurAsync("USD", cancellationToken);
        if (rate.RateToEur is not { } usdToEur || usdToEur == 0m)
        {
            return StatusCode(502, rate.Error ?? "Could not get EUR/USD rate");
        }

        var eurUsd = 1m / usdToEur;

        return Ok(new { eurUsd });
    }

    [HttpGet("{symbol}")]
    public async Task<IActionResult> GetPrice(
        string symbol,
        [FromQuery] string? exchange,
        [FromQuery] string? finanzenNetSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || !System.Text.RegularExpressions.Regex.IsMatch(symbol, @"^[A-Za-z0-9.\-]{1,20}$"))
            return BadRequest("Invalid symbol");

        if (!StockExchanges.TryNormalize(exchange, out var normalizedExchange))
            return BadRequest("Unsupported exchange.");

        // Validate slug if provided to prevent URL injection before any other work
        if (!string.IsNullOrWhiteSpace(finanzenNetSlug) &&
            !FinanzenNetQuoteService.IsValidSlug(finanzenNetSlug))
        {
            return BadRequest("Invalid finanzen.net slug. Only lowercase letters, digits, hyphens, and underscores are allowed.");
        }

        // Resolve the provider symbol for the given exchange.
        // Bare Frankfurt tickers (no period) get ".F" appended so Yahoo returns the
        // correct Frankfurt-listed instrument rather than the US-market listing.
        var providerSymbol = StockExchanges.ResolveProviderSymbol(symbol, normalizedExchange);

        try
        {
            string? currency;
            string? estimateCurrency;
            decimal currentPrice;
            decimal previousClose;
            decimal percentChange;
            string marketState;
            string priceSession;
            DateTime? priceTimestampUtc;
            string? delayWarning = null;
            decimal? rawDayHigh = null;
            decimal? rawDayLow = null;

            if (normalizedExchange == StockExchanges.Frankfurt)
            {
                var quoteResult = await _yahooQuoteService.GetQuoteAsync(providerSymbol, cancellationToken);
                if (!quoteResult.IsSuccess || quoteResult.Quote is null)
                {
                    return StatusCode(quoteResult.StatusCode, quoteResult.ErrorMessage ?? "Could not fetch current quote.");
                }

                var quote = quoteResult.Quote;
                currency = quote.Currency;
                estimateCurrency = quote.EstimateCurrency;
                currentPrice = quote.CurrentPrice;
                previousClose = quote.PreviousClose;
                percentChange = quote.PercentChange;
                marketState = quote.MarketState;
                priceSession = quote.PriceSession;
                priceTimestampUtc = quote.PriceTimestampUtc;
                delayWarning = quote.IsDelayed ? quote.DelayReason : null;
                rawDayHigh = quote.DayHigh;
                rawDayLow = quote.DayLow;
            }
            else
            {
                var quoteResult = await _finnhubQuoteService.GetQuoteAsync(symbol, cancellationToken);
                if (!quoteResult.IsSuccess || quoteResult.Quote is null)
                {
                    return StatusCode(quoteResult.StatusCode, quoteResult.ErrorMessage ?? "Could not fetch current quote.");
                }

                var quote = quoteResult.Quote;
                currency = quote.Currency;
                estimateCurrency = quote.EstimateCurrency;
                currentPrice = quote.CurrentPrice;
                previousClose = quote.PreviousClose;
                percentChange = quote.PercentChange;
                marketState = quote.MarketState;
                priceSession = quote.PriceSession;
                priceTimestampUtc = quote.QuoteTimestampUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(quote.QuoteTimestampUnix).UtcDateTime
                    : null;
                rawDayHigh = quote.DayHigh;
                rawDayLow = quote.DayLow;
            }

            // Optional pre-market enrichment via finanzen.net (experimental, disabled by default).
            // Only replaces the primary quote when:
            //   - The provider is enabled in configuration
            //   - A valid slug was provided for this instrument
            //   - The page explicitly and unambiguously labels the price as pre-market ("PRE")
            // Any failure (timeout, 403, changed markup, ambiguous data) is silently ignored
            // and the existing Yahoo/Finnhub result is preserved.

            string? priceSource = null;

            /*
            if (_finanzenNetQuoteService.IsEnabled &&
                !string.IsNullOrWhiteSpace(finanzenNetSlug))
            {
                try
                {
                    var fnResult = await _finanzenNetQuoteService.GetPreMarketQuoteAsync(
                        finanzenNetSlug, cancellationToken);

                    if (fnResult.IsSuccess &&
                        fnResult.Quote is { } fnQuote &&
                        fnQuote.PriceSession == "PRE" &&
                        fnQuote.Price > 0m)
                    {
                        // Use the pre-market price; keep previousClose from the primary provider
                        // so percent-change is computed against the last official close.
                        var fnPercentChange = previousClose > 0m
                            ? (fnQuote.Price - previousClose) / previousClose * 100m
                            : 0m;

                        currentPrice = fnQuote.Price;
                        percentChange = fnPercentChange;
                        priceSession = "PRE";
                        priceTimestampUtc = fnQuote.ProviderTimestampUtc;
                        priceSource = fnQuote.Source;
                        // Currency from finanzen.net overrides primary only when provided
                        if (!string.IsNullOrWhiteSpace(fnQuote.Currency))
                        {
                            currency = fnQuote.Currency;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // FinanzenNet enrichment failure must not fail the primary quote endpoint.
                    // The exception is already logged inside the service.
                }
            }
            */

            var conversionContext = await _stockQuoteConversionService.GetConversionContextAsync(
                currency,
                estimateCurrency,
                cancellationToken);


            //try
            //{
            //    await _priceEventPublisher.PublishAsync(
            //        new PriceUpdatedEvent(
            //            Guid.NewGuid(),
            //            symbol));
            //}
            //catch (Exception ex)
            //{
            //    _logger.LogWarning(
            //        ex,
            //        "Could not publish PriceUpdatedEvent for {Symbol}",
            //        symbol);
            //}

            return Ok(_stockQuoteConversionService.BuildQuoteResponse(
                providerSymbol,
                currentPrice,
                previousClose,
                percentChange,
                marketState,
                conversionContext,
                priceSession,
                priceTimestampUtc,
                priceSource,
                delayWarning,
                rawDayHigh,
                rawDayLow));
        }
        catch (Exception)
        {
            return StatusCode(500, "Error fetching price.");
        }
    }
}
