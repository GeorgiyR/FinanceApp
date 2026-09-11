using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace FinanceApp.API.Services;

/// <summary>
/// Configuration options for the experimental finanzen.net quote provider.
/// This provider is disabled by default and must only be enabled in development.
/// </summary>
/// <remarks>
/// IMPORTANT: Before enabling this provider, the operator must:
/// <list type="bullet">
///   <item>Review <c>https://www.finanzen.net/robots.txt</c> and verify that automated
///   access to the <c>/aktien/</c> path is not disallowed for the configured User-Agent.</item>
///   <item>Review finanzen.net terms of service regarding automated data access.</item>
///   <item>Ensure compliance with all applicable usage restrictions.</item>
///   <item>Disable immediately if blocked, rate-limited, or if markup changes break parsing.</item>
/// </list>
/// </remarks>
public sealed class FinanzenNetOptions
{
    /// <summary>
    /// Whether the provider is active. Defaults to <c>false</c>.
    /// Only enable in development after verifying robots.txt and terms of service.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL for finanzen.net. Defaults to <c>https://www.finanzen.net</c>.</summary>
    public string BaseUrl { get; set; } = "https://www.finanzen.net";

    /// <summary>How long a successfully parsed pre-market quote is cached. Defaults to 5 minutes.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum interval between outgoing HTTP requests to finanzen.net.
    /// Enforced process-wide to avoid hammering the server. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>HTTP request timeout. Defaults to 15 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Optional User-Agent header value sent with requests.
    /// Should identify the application as a development/research tool.
    /// Never include personal information or production credentials.
    /// </summary>
    public string? UserAgent { get; set; }
}

/// <summary>
/// Result of a finanzen.net pre-market quote request.
/// </summary>
public sealed record FinanzenNetQuoteResult(
    FinanzenNetQuoteData? Quote,
    int StatusCode,
    string? ErrorMessage)
{
    public bool IsSuccess => Quote is not null;

    public static FinanzenNetQuoteResult Success(FinanzenNetQuoteData quote) =>
        new(quote, StatusCodes.Status200OK, null);

    public static FinanzenNetQuoteResult Failure(int statusCode, string errorMessage) =>
        new(null, statusCode, errorMessage);
}

/// <summary>
/// A quote obtained from finanzen.net.
/// </summary>
/// <param name="Price">The price value. Always positive and finite.</param>
/// <param name="Currency">ISO currency code when available.</param>
/// <param name="ProviderTimestampUtc">
/// Timestamp as reported by the provider. Never substituted with request time.
/// Null when the source did not provide a reliable, unambiguous timestamp.
/// </param>
/// <param name="PriceSession">
/// <c>"PRE"</c> only when the source page explicitly and unambiguously identifies the
/// extracted price as a pre-market price. <c>"LAST"</c> otherwise.
/// Never inferred from clock time or market schedule.
/// </param>
/// <param name="Venue">Exchange or venue name when available.</param>
/// <param name="Source">Fixed to <c>"finanzen.net"</c>.</param>
public sealed record FinanzenNetQuoteData(
    decimal Price,
    string? Currency,
    DateTime? ProviderTimestampUtc,
    string PriceSession,
    string? Venue,
    string Source = "finanzen.net");

/// <summary>
/// Experimental finanzen.net quote service interface.
/// </summary>
public interface IFinanzenNetQuoteService
{
    /// <summary>Whether this provider is enabled in configuration.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Attempts to retrieve a pre-market quote for the given finanzen.net slug.
    /// </summary>
    /// <param name="slug">
    /// A validated finanzen.net instrument slug such as <c>microsoft-aktie</c>.
    /// Must consist only of lowercase letters, digits, and hyphens.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing pre-market data or an error description.</returns>
    Task<FinanzenNetQuoteResult> GetPreMarketQuoteAsync(string slug, CancellationToken cancellationToken = default);
}

/// <summary>
/// Experimental, development-only finanzen.net quote service.
/// Parses pre-market prices from finanzen.net stock pages using AngleSharp.
/// </summary>
/// <remarks>
/// This service is disabled by default (<see cref="FinanzenNetOptions.Enabled"/> = false).
/// It is designed to fail safely: provider failure, changed markup, or ambiguous data
/// will return a failure result without affecting other quote providers.
/// <para>
/// Pre-market prices are only returned when the page explicitly and unambiguously
/// labels the extracted value as pre-market (German: "Vorbörslich").
/// Session labels are never inferred from the current clock time.
/// </para>
/// </remarks>
public sealed class FinanzenNetQuoteService : IFinanzenNetQuoteService
{
    /// <summary>
    /// Slug validation pattern. Only lowercase letters, digits, hyphens, and underscores.
    /// Must start with a letter or digit. Max 120 characters.
    /// No slashes, dots, or other characters that could cause URL/path injection.
    /// </summary>
    private static readonly Regex SlugPattern =
        new(@"^[a-z0-9][a-z0-9_-]{0,119}$", RegexOptions.Compiled);

    /// <summary>
    /// German pre-market labels recognized by the parser.
    /// Matched case-insensitively after trimming whitespace.
    /// </summary>
    private static readonly string[] PreMarketLabels =
        ["vorbörslich", "pre-market", "premarket", "vorboerslich"];

    /// <summary>
    /// German number format: dot as thousands separator, comma as decimal separator.
    /// Example: "1.234,56" → 1234.56
    /// </summary>
    private static readonly NumberFormatInfo GermanNumberFormat = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        CurrencyDecimalSeparator = ",",
        CurrencyGroupSeparator = ".",
    };

    private static readonly HtmlParser HtmlParser = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly FinanzenNetOptions _options;
    private readonly ILogger<FinanzenNetQuoteService> _logger;

    /// <summary>
    /// Timestamp of the last outgoing HTTP request, used for process-level throttling.
    /// Protected by <see cref="_throttleLock"/>.
    /// </summary>
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);

    public FinanzenNetQuoteService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IOptions<FinanzenNetOptions> options,
        ILogger<FinanzenNetQuoteService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled;

    /// <inheritdoc />
    public async Task<FinanzenNetQuoteResult> GetPreMarketQuoteAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "FinanzenNet quote provider is disabled.");
        }

        if (!IsValidSlug(slug))
        {
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status400BadRequest,
                "Invalid finanzen.net slug. Only lowercase letters, digits, hyphens, and underscores are allowed.");
        }

        var cacheKey = $"finanzennet:premarket:{slug}";
        if (_memoryCache.TryGetValue(cacheKey, out FinanzenNetQuoteData? cached) && cached is not null)
        {
            _logger.LogDebug("FinanzenNet pre-market quote for {Slug} served from cache.", SanitizeForLog(slug));
            return FinanzenNetQuoteResult.Success(cached);
        }

        var html = await FetchPageAsync(slug, cancellationToken);
        if (html is null)
        {
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status502BadGateway,
                "Failed to fetch finanzen.net page.");
        }

        var parseResult = await ParsePreMarketQuoteAsync(html, slug);
        if (!parseResult.IsSuccess || parseResult.Quote is null)
        {
            return parseResult;
        }

        _memoryCache.Set(cacheKey, parseResult.Quote, _options.CacheDuration);
        return parseResult;
    }

    /// <summary>
    /// Returns <c>true</c> when the slug is safe to embed in a URL path.
    /// </summary>
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && SlugPattern.IsMatch(slug);

    /// <summary>
    /// Strips newline and carriage-return characters from a string before it is written to a log
    /// message to prevent log-forging (CWE-117).
    /// </summary>
    private static string SanitizeForLog(string value) =>
        value.Replace('\n', '_').Replace('\r', '_');

    private async Task<string?> FetchPageAsync(string slug, CancellationToken cancellationToken)
    {
        await _throttleLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRequestUtc;
            if (elapsed < _options.MinRequestInterval)
            {
                var delay = _options.MinRequestInterval - elapsed;
                _logger.LogDebug(
                    "FinanzenNet throttle: waiting {DelayMs}ms before requesting slug {Slug}.",
                    (int)delay.TotalMilliseconds, SanitizeForLog(slug));
                await Task.Delay(delay, cancellationToken);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _throttleLock.Release();
        }

        var baseUrl = (_options.BaseUrl ?? "https://www.finanzen.net").TrimEnd('/');
        var url = $"{baseUrl}/aktien/{Uri.EscapeDataString(slug)}";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = _options.RequestTimeout;

        var userAgent = !string.IsNullOrWhiteSpace(_options.UserAgent)
            ? _options.UserAgent
            : "FinanceApp-Dev/1.0 (development research tool; not for production use)";
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9");

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);

            if ((int)response.StatusCode == StatusCodes.Status403Forbidden ||
                (int)response.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                _logger.LogWarning(
                    "FinanzenNet returned {StatusCode} for slug {Slug}. " +
                    "Verify robots.txt and terms of service; disable the provider if access is disallowed.",
                    (int)response.StatusCode, SanitizeForLog(slug));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FinanzenNet request failed with status {StatusCode} for slug {Slug}.",
                    (int)response.StatusCode, SanitizeForLog(slug));
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "FinanzenNet request timed out for slug {Slug}.", SanitizeForLog(slug));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "FinanzenNet request failed for slug {Slug}.", SanitizeForLog(slug));
            return null;
        }
    }

    private async Task<FinanzenNetQuoteResult> ParsePreMarketQuoteAsync(string html, string slug)
    {
        try
        {
            using var document = await HtmlParser.ParseDocumentAsync(html);
            return ParseDocument(document, slug);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FinanzenNet HTML parsing failed for slug {Slug}.", SanitizeForLog(slug));
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status502BadGateway,
                "Failed to parse finanzen.net page.");
        }
    }

    /// <summary>
    /// Parses the document to find an explicitly labeled pre-market price.
    /// Returns failure if:
    /// <list type="bullet">
    ///   <item>No pre-market section is found.</item>
    ///   <item>Multiple pre-market sections are found (ambiguous).</item>
    ///   <item>The price cannot be parsed safely.</item>
    ///   <item>The value is zero, negative, or implausible.</item>
    /// </list>
    /// </summary>
    internal static FinanzenNetQuoteResult ParseDocument(IDocument document, string slug)
    {
        // Strategy: find all elements whose text content explicitly names a pre-market session.
        // Only accept the result when exactly one such element exists, and the price/currency
        // can be extracted from the same containing quote block.

        var preMarketContainers = FindPreMarketContainers(document).ToList();

        if (preMarketContainers.Count == 0)
        {
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status404NotFound,
                "No explicitly labeled pre-market price found on finanzen.net page.");
        }

        if (preMarketContainers.Count > 1)
        {
            // Ambiguous: multiple containers claim to be pre-market. Reject to avoid mismatching
            // price, currency, and timestamp from different venues.
            return FinanzenNetQuoteResult.Failure(
                StatusCodes.Status422UnprocessableEntity,
                "Ambiguous finanzen.net page: multiple pre-market price sections found. Rejecting to avoid mismatched data.");
        }

        var container = preMarketContainers[0];
        return ExtractQuoteFromContainer(container, slug);
    }

    /// <summary>
    /// Finds elements that are explicitly labeled as pre-market quote containers.
    /// Returns each top-level container element that contains a pre-market label
    /// paired with a price value.
    /// </summary>
    private static IEnumerable<IElement> FindPreMarketContainers(IDocument document)
    {
        // Look for elements that:
        // 1. Have text matching a pre-market label, AND
        // 2. Are inside (or are) a quote-card-like block containing a price value.
        //
        // We search broadly for labeled elements, then walk up to find the enclosing quote block.
        // Deduplication ensures each unique container is returned only once.

        var seen = new HashSet<IElement>(ReferenceEqualityComparer.Instance);

        // Query text-bearing elements that could plausibly be a label (not container divs,
        // which would include descendant text and falsely match the pre-market keyword).
        var candidates = document.QuerySelectorAll(
            "span, h2, h3, h4, th, td, label, p, dt, caption");

        foreach (var element in candidates)
        {
            var text = GetNormalizedText(element);
            if (!IsPreMarketLabel(text))
            {
                continue;
            }

            // Walk up to find an enclosing quote block that also contains a price.
            var container = FindEnclosingQuoteContainer(element, document);
            if (container is not null && !seen.Contains(container))
            {
                seen.Add(container);
                yield return container;
            }
        }
    }

    /// <summary>
    /// Walks up the DOM from <paramref name="labelElement"/> to find an enclosing
    /// block that contains a price value element.
    /// </summary>
    private static IElement? FindEnclosingQuoteContainer(IElement labelElement, IDocument document)
    {
        var current = labelElement.ParentElement;
        // Walk up at most 6 levels
        for (var depth = 0; depth < 6 && current is not null && current != document.DocumentElement; depth++)
        {
            // Check if this element contains a price-bearing child
            var priceElement = FindPriceElement(current);
            if (priceElement is not null)
            {
                return current;
            }

            current = current.ParentElement;
        }

        return null;
    }

    /// <summary>
    /// Finds the first child element of <paramref name="container"/> that looks like
    /// a price element by CSS class/attribute, or falls back to text-based detection.
    /// Does NOT require the value to be parseable (parsing happens in the extraction step).
    /// </summary>
    private static IElement? FindPriceElement(IElement container)
    {
        // Try class-based and attribute-based selectors first (reliable structure indicators).
        // These do NOT require the text to be parseable — malformed values are caught later.
        var byClass = container.QuerySelector(
            "[data-value], .snapshot__value, .snapshot-price, .price__value, .quote-value");
        if (byClass is not null)
        {
            return byClass;
        }

        // Try table cell selector with parseable text check
        var byTable = container.QuerySelectorAll("td.text-right");
        foreach (var el in byTable)
        {
            if (LooksLikePrice(el))
            {
                return el;
            }
        }

        // Fallback: find any span/td element whose text looks like a German decimal number
        return container
            .QuerySelectorAll("span, td")
            .FirstOrDefault(el => IsStandaloneElement(el, container) && LooksLikePrice(el));
    }

    /// <summary>Returns <c>true</c> when the element is a direct or near-direct child of the container.</summary>
    private static bool IsStandaloneElement(IElement element, IElement container)
    {
        var parent = element.ParentElement;
        for (var i = 0; i < 3 && parent is not null; i++)
        {
            if (ReferenceEquals(parent, container))
            {
                return true;
            }

            parent = parent.ParentElement;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the element text looks like a parsable German decimal number.
    /// </summary>
    private static bool LooksLikePrice(IElement element)
    {
        var text = GetNormalizedText(element);
        return TryParseGermanDecimal(text, out var value) && value > 0m;
    }

    /// <summary>
    /// Extracts a <see cref="FinanzenNetQuoteData"/> from the given pre-market container.
    /// </summary>
    private static FinanzenNetQuoteResult ExtractQuoteFromContainer(IElement container, string slug)
    {
        // 1. Find the price value.
        // data-value attributes use invariant format; visible text uses German locale.
        decimal price;
        var dataValueEl = container.QuerySelector("[data-value]");
        if (dataValueEl is not null)
        {
            var dataValue = dataValueEl.GetAttribute("data-value")?.Trim();
            if (!string.IsNullOrWhiteSpace(dataValue) &&
                decimal.TryParse(dataValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var dv) &&
                dv > 0m)
            {
                price = dv;
            }
            else if (!string.IsNullOrWhiteSpace(dataValue) &&
                     TryParseGermanDecimal(dataValue, out var dvGerman) &&
                     dvGerman > 0m)
            {
                price = dvGerman;
            }
            else
            {
                return FinanzenNetQuoteResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "Pre-market price value is zero, negative, or could not be parsed.");
            }
        }
        else
        {
            var priceText = GetPriceText(container);
            if (priceText is null)
            {
                return FinanzenNetQuoteResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "Pre-market container found but could not locate a price value.");
            }

            if (!TryParseGermanDecimal(priceText, out var parsedPrice) || parsedPrice <= 0m)
            {
                return FinanzenNetQuoteResult.Failure(
                    StatusCodes.Status502BadGateway,
                    "Pre-market price value is zero, negative, or could not be parsed.");
            }

            price = parsedPrice;
        }

        // 2. Extract currency
        var currency = ExtractCurrency(container);

        // 3. Extract timestamp — must come from the same container; null if absent
        var timestamp = ExtractTimestamp(container);

        // 4. Extract venue
        var venue = ExtractVenue(container);

        // Price is explicitly labeled pre-market: PriceSession = "PRE"
        var data = new FinanzenNetQuoteData(
            Price: price,
            Currency: currency,
            ProviderTimestampUtc: timestamp,
            PriceSession: "PRE",
            Venue: venue);

        return FinanzenNetQuoteResult.Success(data);
    }

    /// <summary>Reads the raw text of the price element within <paramref name="container"/>.</summary>
    private static string? GetPriceText(IElement container)
    {
        var priceEl = FindPriceElement(container);
        return priceEl is not null ? GetNormalizedText(priceEl) : null;
    }

    /// <summary>
    /// Extracts the currency from the container. Looks for ISO codes and common German symbols.
    /// Returns a normalized ISO code (e.g. <c>"EUR"</c>) or <c>null</c>.
    /// </summary>
    private static string? ExtractCurrency(IElement container)
    {
        // Check data-currency attribute
        var currencyAttr = container.GetAttribute("data-currency")
            ?? container.QuerySelector("[data-currency]")?.GetAttribute("data-currency");
        if (!string.IsNullOrWhiteSpace(currencyAttr))
        {
            return NormalizeCurrency(currencyAttr);
        }

        // Look for currency elements
        var currencyElement = container.QuerySelector(
            ".snapshot__currency, .currency, [class*='currency']");
        if (currencyElement is not null)
        {
            var text = GetNormalizedText(currencyElement);
            var normalized = NormalizeCurrency(text);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        // Scan all text content for currency patterns
        var allText = container.TextContent;
        return ExtractCurrencyFromText(allText);
    }

    /// <summary>
    /// Extracts a provider timestamp from within the container.
    /// Returns <c>null</c> if no reliable timestamp is found.
    /// The timestamp is never substituted with the current time.
    /// </summary>
    private static DateTime? ExtractTimestamp(IElement container)
    {
        // data-time attribute (Unix seconds or ISO string)
        var timeAttr = container.GetAttribute("data-time")
            ?? container.QuerySelector("[data-time]")?.GetAttribute("data-time");
        if (!string.IsNullOrWhiteSpace(timeAttr))
        {
            if (long.TryParse(timeAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) && unix > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            if (DateTimeOffset.TryParse(timeAttr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                return dto.UtcDateTime;
            }
        }

        // Look for time elements (<time datetime="...">)
        var timeElement = container.QuerySelector("time[datetime]");
        if (timeElement is not null)
        {
            var dt = timeElement.GetAttribute("datetime");
            if (!string.IsNullOrWhiteSpace(dt) &&
                DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDto))
            {
                return parsedDto.UtcDateTime;
            }
        }

        // Look for time-class elements with German time format (HH:mm:ss or HH:mm)
        var timeClassEl = container.QuerySelector(".snapshot__time, .time, [class*='time']");
        if (timeClassEl is not null)
        {
            var timeText = GetNormalizedText(timeClassEl);
            return ParseGermanTime(timeText);
        }

        return null;
    }

    /// <summary>
    /// Extracts venue/exchange name from the container.
    /// </summary>
    private static string? ExtractVenue(IElement container)
    {
        var venueEl = container.QuerySelector(".snapshot__exchange, .exchange, [class*='exchange'], [data-exchange]");
        if (venueEl is not null)
        {
            var text = GetNormalizedText(venueEl);
            if (!string.IsNullOrWhiteSpace(text) && !IsPreMarketLabel(text))
            {
                return text;
            }
        }

        var dataExchange = container.GetAttribute("data-exchange");
        if (!string.IsNullOrWhiteSpace(dataExchange))
        {
            return dataExchange.Trim();
        }

        return null;
    }

    /// <summary>
    /// Parses a German time string (e.g. <c>"08:12:34"</c> or <c>"08:12"</c>) into a UTC DateTime.
    /// The date component defaults to today (UTC) since finanzen.net typically shows today's pre-market time.
    /// Returns <c>null</c> if parsing fails; time-only strings without a date cannot be made reliable.
    /// </summary>
    /// <remarks>
    /// This method returns <c>null</c> intentionally: pairing a time-only string with "today"
    /// could produce a wrong UTC datetime near midnight or across date boundaries.
    /// Only full datetime values (with date) are returned.
    /// </remarks>
    private static DateTime? ParseGermanTime(string? text)
    {
        // Intentionally return null for time-only strings.
        // A time without a date cannot be reliably converted to UTC without knowing the timezone,
        // and pairing with "today" could be wrong at midnight or in different timezones.
        // The requirement says missing timestamp must remain null.
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Accept full datetime strings
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return dto.UtcDateTime;
        }

        // Time-only strings → null (see remarks above)
        return null;
    }

    /// <summary>Returns <c>true</c> when <paramref name="text"/> matches a known pre-market label.</summary>
    private static bool IsPreMarketLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        return PreMarketLabels.Any(label =>
            normalized == label ||
            normalized.Contains(label, StringComparison.Ordinal));
    }

    /// <summary>Gets the trimmed, collapsed text content of an element (no inner HTML).</summary>
    private static string GetNormalizedText(IElement element)
    {
        var text = element.TextContent ?? string.Empty;
        // Collapse whitespace
        return string.Join(" ", text.Split(new char[] { '\r', '\n', '\t', ' ' },
            StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>
    /// Tries to parse a German-formatted decimal number.
    /// German format: <c>"1.234,56"</c> (dot = thousands sep, comma = decimal sep).
    /// Also accepts plain integers and numbers without a thousands separator.
    /// </summary>
    internal static bool TryParseGermanDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Strip non-numeric noise: currency symbols, whitespace, plus sign
        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"[€$£+\s%]", string.Empty);
        if (string.IsNullOrEmpty(cleaned))
        {
            return false;
        }

        // Try German format first (comma as decimal separator)
        if (decimal.TryParse(cleaned, NumberStyles.Number, GermanNumberFormat, out value))
        {
            return true;
        }

        // Try invariant format as fallback (dot as decimal separator, no thousands)
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to identify a currency from free text by looking for ISO codes and symbols.
    /// </summary>
    private static string? ExtractCurrencyFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Look for common ISO currency codes as whole words
        var isoCodes = new[] { "EUR", "USD", "GBP", "CHF", "JPY", "CAD", "AUD", "SEK", "NOK", "DKK" };
        foreach (var code in isoCodes)
        {
            if (Regex.IsMatch(text, $@"\b{code}\b", RegexOptions.IgnoreCase))
            {
                return code;
            }
        }

        // Currency symbol mapping
        if (text.Contains('€'))
        {
            return "EUR";
        }

        if (text.Contains('$'))
        {
            return "USD";
        }

        if (text.Contains('£'))
        {
            return "GBP";
        }

        return null;
    }

    /// <summary>Normalizes a raw currency string to an uppercase ISO code.</summary>
    private static string? NormalizeCurrency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToUpperInvariant();

        return normalized switch
        {
            "€" or "EUR" => "EUR",
            "$" or "USD" => "USD",
            "£" or "GBP" => "GBP",
            "CHF" => "CHF",
            "JPY" => "JPY",
            "CAD" => "CAD",
            "AUD" => "AUD",
            "SEK" => "SEK",
            "NOK" => "NOK",
            "DKK" => "DKK",
            _ => null
        };
    }
}
