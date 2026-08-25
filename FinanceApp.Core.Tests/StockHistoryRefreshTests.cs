using System.Net;
using System.Text;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class StockHistoryRefreshTests
{
    [Fact]
    public async Task GetHistoryAsync_CatalogOnlyStockWithPersistedHistoryAndNoMemberships_ReturnsStoredPoints()
    {
        await using var context = CreateInMemoryContext();
        var now = DateTime.UtcNow;
        var stock = new Stock
        {
            Id = 1,
            Ticker = "MCD",
            Exchange = StockExchanges.Nyse,
            Name = "McDonald's",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1wk",
            Timestamp = now.AddDays(-7),
            Open = 300m,
            High = 305m,
            Low = 299m,
            Close = 304m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m,
            Volume = 1000
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler();
        var service = CreateService(context, handler);

        var response = await service.GetHistoryAsync(stock, "1y");

        Assert.Equal("1y", response.Range);
        Assert.Equal("1wk", response.Interval);
        Assert.Single(response.Points);
        Assert.Equal(304m, response.Points[0].CloseRaw);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(await context.StockMarketIndices.ToListAsync());
        Assert.Equal(StockTrackingStatus.CatalogOnly, await context.Stocks.Select(x => x.TrackingStatus).SingleAsync());
    }

    [Fact]
    public async Task GetHistoryAsync_CatalogOnlyStockWithoutStoredHistory_PerformsOnDemandSync()
    {
        await using var context = CreateInMemoryContext();
        var now = DateTime.UtcNow;
        var stock = new Stock
        {
            Id = 1,
            Ticker = "MCD",
            Exchange = StockExchanges.Nyse,
            Name = "McDonald's",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(ToUnix(now.AddDays(-1)), 10m),
            SuccessChartJson(ToUnix(now.AddDays(-7)), 20m),
            SuccessChartJson(ToUnix(now.AddDays(-1)), 30m),
            SuccessChartJson(ToUnix(now.AddHours(-6)), 40m),
            SuccessChartJson(ToUnix(now.AddMinutes(-30)), 50m));
        var service = CreateService(context, handler);

        var response = await service.GetHistoryAsync(stock, "6m");

        Assert.Equal(3, handler.CallCount);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=1d", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=1h", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=5m", StringComparison.Ordinal));
        Assert.Single(response.Points);
        Assert.Equal(10m, response.Points[0].CloseRaw);
        Assert.True(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.Interval == "1d"));
        Assert.Equal(StockTrackingStatus.CatalogOnly, await context.Stocks.Select(x => x.TrackingStatus).SingleAsync());
        Assert.Equal(1, await context.Stocks.CountAsync());
        Assert.Empty(await context.StockMarketIndices.ToListAsync());
    }

    [Theory]
    [InlineData(StockExchanges.Nyse)]
    [InlineData(StockExchanges.Nasdaq)]
    [InlineData(StockExchanges.Frankfurt)]
    public async Task GetHistoryAsync_LongRangeFallbackFromDailyRows_ReturnsNonEmptyFor1y3y5y(string exchange)
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 11,
            Ticker = "WPM",
            Exchange = exchange,
            Name = "WPM"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            CreateDailyRow(stock.Id, new DateTime(2026, 7, 15, 20, 0, 0, DateTimeKind.Utc), 9m, 10m, 8m, 9.5m, 90L, 9.1m),
            CreateDailyRow(stock.Id, new DateTime(2026, 7, 31, 20, 0, 0, DateTimeKind.Utc), 9.5m, 10.5m, 9m, 10m, 95L, 9.8m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc), 10m, 15m, 9m, 11m, 100L, 10.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc), 11m, 16m, 10m, 12m, 110L, 11.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc), 12m, 17m, 11m, 13m, 120L, 12.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 12, 20, 0, 0, DateTimeKind.Utc), 12m, 18m, 11m, 14m, 130L, 13.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 13, 20, 0, 0, DateTimeKind.Utc), 14m, 19m, 13m, 15m, 140L, 14.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 17, 20, 0, 0, DateTimeKind.Utc), 16m, 20m, 15m, 17m, 150L, 16.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 18, 20, 0, 0, DateTimeKind.Utc), 17m, 21m, 16m, 18m, 160L, 17.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 19, 20, 0, 0, DateTimeKind.Utc), 18m, 22m, 17m, 19m, 170L, 18.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 20, 20, 0, 0, DateTimeKind.Utc), 19m, 23m, 18m, 20m, 180L, 19.5m),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 21, 20, 0, 0, DateTimeKind.Utc), 20m, 24m, 19m, 21m, 190L, 20.5m));
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(now)));

        var oneYear = await service.GetHistoryAsync(stock, "1y");
        Assert.Equal("1wk", oneYear.Interval);
        Assert.True(oneYear.Points.Count >= 3);
        var dedupedWeek = Assert.Single(oneYear.Points, point => point.CloseRaw == 15m);
        Assert.Equal(10m, dedupedWeek.OpenRaw);
        Assert.Equal(19m, dedupedWeek.HighRaw);
        Assert.Equal(9m, dedupedWeek.LowRaw);
        Assert.Equal(480L, dedupedWeek.Volume);
        Assert.Null(oneYear.UnavailableReason);
        Assert.Equal(oneYear.Points[^1].Timestamp, oneYear.AsOfUtc);

        var threeYears = await service.GetHistoryAsync(stock, "3y");
        Assert.Equal("1mo", threeYears.Interval);
        Assert.NotEmpty(threeYears.Points);
        Assert.Null(threeYears.UnavailableReason);

        var fiveYears = await service.GetHistoryAsync(stock, "5y");
        Assert.Equal("1mo", fiveYears.Interval);
        Assert.NotEmpty(fiveYears.Points);
        Assert.Null(fiveYears.UnavailableReason);
        Assert.Equal(fiveYears.Points[^1].Timestamp, fiveYears.AsOfUtc);
    }

    [Theory]
    [InlineData(StockExchanges.Nyse)]
    [InlineData(StockExchanges.Nasdaq)]
    public async Task GetHistoryAsync_LongRangeFallback_UsesDeterministicUtcBuckets(string exchange)
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        var stock = new Stock { Id = 12, Ticker = "UTC", Exchange = exchange, Name = "UTC Buckets" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            CreateDailyRow(stock.Id, new DateTime(2026, 7, 31, 20, 0, 0, DateTimeKind.Utc), 99m, 100m, 98m, 99m, 9L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc), 100m, 101m, 99m, 100m, 10L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc), 101m, 102m, 100m, 101m, 11L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 12, 20, 0, 0, DateTimeKind.Utc), 102m, 103m, 101m, 102m, 12L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 17, 20, 0, 0, DateTimeKind.Utc), 103m, 104m, 102m, 103m, 13L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 18, 20, 0, 0, DateTimeKind.Utc), 104m, 105m, 103m, 104m, 14L),
            CreateDailyRow(stock.Id, new DateTime(2026, 8, 19, 20, 0, 0, DateTimeKind.Utc), 105m, 106m, 104m, 105m, 15L));
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(now)));
        var oneYear = await service.GetHistoryAsync(stock, "1y");
        var threeYears = await service.GetHistoryAsync(stock, "3y");

        Assert.Equal(new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), oneYear.Points[0].Timestamp);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), threeYears.Points[0].Timestamp);
    }

    [Theory]
    [InlineData(StockExchanges.Nyse)]
    [InlineData(StockExchanges.Nasdaq)]
    [InlineData(StockExchanges.Frankfurt)]
    public async Task GetHistoryAsync_LongRangeFallback_WithoutSuitableDailyRows_ReturnsUnavailable(string exchange)
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock
        {
            Id = 13,
            Ticker = string.Empty,
            Exchange = exchange,
            Name = "No Data"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(CreateDailyRow(stock.Id, new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc), 10m, 10m, 10m, 10m, 10L, isQuoteDerived: true));
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc))));
        var response = await service.GetHistoryAsync(stock, "1y");

        Assert.Empty(response.Points);
        Assert.Null(response.AsOfUtc);
        Assert.NotNull(response.UnavailableReason);
    }

    [Fact]
    public async Task GetHistoryAsync_Stale24hHistoryDuringOpenSession_RefreshesIntradayOnce()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 15, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = now.AddHours(-4),
            Open = 100m,
            High = 100m,
            Low = 100m,
            Close = 100m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(ToUnix(now.AddDays(-1)), 90m),
            SuccessChartJson(ToUnix(now.AddHours(-1)), 110m),
            SuccessChartJson(ToUnix(now.AddMinutes(-10)), 120m));
        var service = CreateService(
            context,
            handler,
            new FixedTimeProvider(new DateTimeOffset(now)),
            new StockHistoryRefreshOptions { OnDemandIntradayRefreshMinInterval = TimeSpan.FromHours(1) });

        var response = await service.GetHistoryAsync(stock, "24h");

        Assert.Equal(3, handler.CallCount);
        Assert.False(response.IsPotentiallyStale);
        Assert.Equal("10m", response.Interval);
        Assert.Contains(response.Points, point => point.CloseRaw == 120m);
    }

    [Fact]
    public async Task GetHistoryAsync_Fresh24hHistory_DoesNotRefetch()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 15, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = now.AddMinutes(-20),
            Open = 101m,
            High = 101m,
            Low = 101m,
            Close = 101m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler();
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var response = await service.GetHistoryAsync(stock, "24h");

        Assert.Equal(0, handler.CallCount);
        Assert.False(response.IsPotentiallyStale);
        Assert.Single(response.Points);
        Assert.Equal(101m, response.Points[0].CloseRaw);
    }

    [Fact]
    public async Task GetHistoryAsync_ClosedWeekendMarket_DoesNotLoopRefreshStaleIntraday()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc); // Saturday
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = now.AddHours(-20),
            Open = 98m,
            High = 98m,
            Low = 98m,
            Close = 98m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler();
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var first = await service.GetHistoryAsync(stock, "24h");
        var second = await service.GetHistoryAsync(stock, "24h");

        Assert.Equal(0, handler.CallCount);
        Assert.True(first.IsPotentiallyStale);
        Assert.True(second.IsPotentiallyStale);
    }

    [Fact]
    public async Task GetHistoryAsync_24h_UsesPreviousAndCurrentTradingSessions_NotRollingUtc24Hours()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 24, 15, 30, 0, DateTimeKind.Utc); // Monday during US regular session
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 20, 15, 30, 0, DateTimeKind.Utc), // Thursday
                Open = 90m, High = 90m, Low = 90m, Close = 90m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 21, 13, 30, 0, DateTimeKind.Utc), // Friday open
                Open = 100m, High = 100m, Low = 100m, Close = 100m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 21, 19, 50, 0, DateTimeKind.Utc), // Friday close bucket
                Open = 103m, High = 103m, Low = 103m, Close = 103m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 24, 13, 30, 0, DateTimeKind.Utc), // Monday open
                Open = 104m, High = 104m, Low = 104m, Close = 104m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 24, 15, 20, 0, DateTimeKind.Utc), // Monday current
                Open = 106m, High = 106m, Low = 106m, Close = 106m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m,
                IsQuoteDerived = true
            });
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(now)));
        var response = await service.GetHistoryAsync(stock, "24h");

        Assert.Equal("24h", response.Range);
        Assert.Equal(4, response.Points.Count);
        Assert.DoesNotContain(response.Points, x => x.Timestamp == new DateTime(2026, 8, 20, 15, 30, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 8, 21, 13, 30, 0, DateTimeKind.Utc), response.Points[0].Timestamp);
        Assert.Equal(new DateTime(2026, 8, 24, 15, 20, 0, DateTimeKind.Utc), response.Points[^1].Timestamp);
        Assert.True(response.CurrentSessionHasCandles);
        Assert.NotNull(response.PreviousSessionStartUtc);
        Assert.NotNull(response.CurrentSessionStartUtc);
    }

    [Fact]
    public async Task GetHistoryAsync_Today_RemainsCurrentSessionOnly()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 24, 15, 30, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 21, 19, 50, 0, DateTimeKind.Utc),
                Open = 103m, High = 103m, Low = 103m, Close = 103m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2026, 8, 24, 13, 30, 0, DateTimeKind.Utc),
                Open = 104m, High = 104m, Low = 104m, Close = 104m,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
            });
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(now)));
        var response = await service.GetHistoryAsync(stock, "today");

        Assert.Equal("today", response.Range);
        Assert.Single(response.Points);
        Assert.Equal(new DateTime(2026, 8, 24, 13, 30, 0, DateTimeKind.Utc), response.Points[0].Timestamp);
    }

    [Fact]
    public async Task GetHistoryAsync_24h_PreOpenCurrentSessionWithoutCandles_ReportsNoCurrentSessionCandles()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 24, 11, 0, 0, DateTimeKind.Utc); // Monday pre-open for US market
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = new DateTime(2026, 8, 21, 19, 50, 0, DateTimeKind.Utc),
            Open = 103m, High = 103m, Low = 103m, Close = 103m,
            QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, new CountingHandler(), new FixedTimeProvider(new DateTimeOffset(now)));
        var response = await service.GetHistoryAsync(stock, "24h");

        Assert.Single(response.Points);
        Assert.Equal(new DateTime(2026, 8, 21, 19, 50, 0, DateTimeKind.Utc), response.Points[0].Timestamp);
        Assert.False(response.CurrentSessionHasCandles);
    }

    [Fact]
    public async Task GetHistoryAsync_RateLimitedRefresh_PreservesOldIntradayAndReturnsStaleWarning()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 15, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AMD",
            Exchange = StockExchanges.Nasdaq,
            Name = "AMD US"
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = now.AddHours(-4),
            Open = 97m,
            High = 97m,
            Low = 97m,
            Close = 97m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new StatusSequenceHandler(HttpStatusCode.TooManyRequests);
        var service = CreateService(
            context,
            handler,
            new FixedTimeProvider(new DateTimeOffset(now)),
            new StockHistoryRefreshOptions { OnDemandIntradayRefreshMinInterval = TimeSpan.FromHours(1) });

        var response = await service.GetHistoryAsync(stock, "24h");
        await service.GetHistoryAsync(stock, "24h");

        Assert.Equal(1, handler.CallCount);
        Assert.Single(response.Points);
        Assert.Equal(97m, response.Points[0].CloseRaw);
        Assert.True(response.IsPotentiallyStale);
        Assert.NotNull(response.StaleReason);
        Assert.Contains("Последнее обновление не удалось", response.StaleReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshHistoryAsync_ReplacesOnlySelectedStockRows_AndUsesCurrentProviderSymbol()
    {
        await using var context = CreateInMemoryContext();
        var target = new Stock { Id = 1, Ticker = "AMZN", Exchange = StockExchanges.Frankfurt, Name = "Amazon FRA" };
        var other = new Stock { Id = 2, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.AddRange(target, other);
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 1, Interval = "1d", Timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Open = 1, High = 1, Low = 1, Close = 1, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = 2, Interval = "1d", Timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Open = 9, High = 9, Low = 9, Close = 9, QuoteUnitMultiplier = 1m });
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            SuccessChartJson(1704067200, 10m),
            SuccessChartJson(1704672000, 20m),
            SuccessChartJson(1705276800, 30m),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        var result = await service.RefreshHistoryAsync(target);

        Assert.Equal(1, result.StockId);
        Assert.Equal(0, result.DeletedPoints);
        Assert.Equal(5, result.ImportedPoints);
        Assert.All(handler.RequestedUrls, url => Assert.Contains("AMZN.F", url, StringComparison.Ordinal));

        var targetRows = await context.StockHistoricalPrices.Where(x => x.StockId == 1).OrderBy(x => x.Interval).ToListAsync();
        Assert.Equal(6, targetRows.Count);
        Assert.Contains(targetRows, row => row.Close == 1m);
        Assert.Contains(targetRows, row => row.Interval == "10m");
        Assert.Equal(1, await context.StockHistoricalPrices.CountAsync(x => x.StockId == 2));
        Assert.Equal(9m, await context.StockHistoricalPrices.Where(x => x.StockId == 2).Select(x => x.Close).SingleAsync());
    }

    [Fact]
    public async Task RefreshHistoryAsync_PersistsAlignedAdjustedClose_WithoutReplacingRawClose()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            SuccessChartJson((1704067200, 10m, 100L, 9m)),
            SuccessChartJson((1704672000, 20m, 100L, 19m)),
            SuccessChartJson((1705276800, 30m, 100L, 29m)),
            SuccessChartJson((1705881600, 40m, 100L, 39m)),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        await service.RefreshHistoryAsync(stock);

        var dailyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == stock.Id && x.Interval == "1d");
        var hourlyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == stock.Id && x.Interval == "1h");
        var tenMinuteRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == stock.Id && x.Interval == "10m");

        Assert.Equal(30m, dailyRow.Close);
        Assert.Equal(29m, dailyRow.AdjustedClose);
        Assert.Equal(39m, hourlyRow.AdjustedClose);
        Assert.Null(tenMinuteRow.AdjustedClose);
    }

    [Fact]
    public async Task RefreshHistoryAsync_MissingAdjustedClose_LeavesAdjustedCloseNull()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            SuccessChartJson(1704067200, 10m),
            SuccessChartJson(1704672000, 20m),
            SuccessChartJson(1705276800, 30m),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        await service.RefreshHistoryAsync(stock);

        var dailyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == stock.Id && x.Interval == "1d");
        Assert.Null(dailyRow.AdjustedClose);
    }

    [Fact]
    public async Task RefreshHistoryAsync_InvalidAdjustedClosePayloads_AreIgnoredSafely()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            RawChartJson("1704067200", "10", "10", "10", "10", "100", "\"adjclose\":[null]"),
            RawChartJson("1704672000", "20", "20", "20", "20", "100", "\"adjclose\":[0]"),
            RawChartJson("1705276800", "30", "30", "30", "30", "100", "\"adjclose\":[-1]"),
            RawChartJson("1705881600", "40", "40", "40", "40", "100", "\"adjclose\":[40,41]"),
            RawChartJson("1706486400", "50", "50", "50", "50", "100", null));
        var service = CreateService(context, handler);

        await service.RefreshHistoryAsync(stock);

        var rows = await context.StockHistoricalPrices
            .Where(x => x.StockId == stock.Id && x.Interval != "10m")
            .OrderBy(x => x.Interval)
            .ToListAsync();

        Assert.All(rows, row => Assert.Null(row.AdjustedClose));
    }

    [Fact]
    public async Task SyncHistoricalDataForStockAsync_UpdatesAdjustedCloseOnExistingTimestampedRows()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(1704067200).UtcDateTime,
            Open = 25m,
            High = 25m,
            Low = 25m,
            Close = 25m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            SuccessChartJson((1704067200, 10m, 100L, 9m)),
            SuccessChartJson(1704672000, 20m),
            SuccessChartJson((1705276800, 30m, 100L, 29m)),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        await service.SyncHistoricalDataForStockAsync(stock);

        var dailyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == stock.Id && x.Interval == "1d");
        Assert.Equal(10m, dailyRow.Close);
        Assert.Equal(9m, dailyRow.AdjustedClose);
    }

    [Fact]
    public async Task RefreshHistoryAsync_WhenProviderReturnsNoData_PreservesExistingHistory()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = 1,
            Interval = "1d",
            Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Open = 7m,
            High = 7m,
            Low = 7m,
            Close = 7m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            SuccessChartJson(1704067200, 10m),
            SuccessChartJson(1704672000, 20m),
            EmptyChartJson(),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        var result = await service.RefreshHistoryAsync(stock);

        Assert.Equal(0, result.DeletedPoints);
        var rows = await context.StockHistoricalPrices.Where(x => x.StockId == 1).OrderBy(x => x.Interval).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.Contains(rows, row => row.Close == 7m && row.Interval == "1d");
    }

    [Fact]
    public async Task RefreshHistoryAsync_RelationalProvider_ReplacesAtomicallyAndDoesNotRepeatProviderCalls()
    {
        await using var harness = await CreateSqliteHarnessAsync();
        var target = new Stock { Id = 1, Ticker = "AMZN", Exchange = StockExchanges.Frankfurt, Name = "Amazon FRA" };
        var other = new Stock { Id = 2, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        harness.Context.Stocks.AddRange(target, other);
        harness.Context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 1, Interval = "1d", Timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Open = 1, High = 1, Low = 1, Close = 1, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = 2, Interval = "1d", Timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), Open = 9, High = 9, Low = 9, Close = 9, QuoteUnitMultiplier = 1m });
        await harness.Context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(1704067200, 10m),
            SuccessChartJson(1704672000, 20m),
            SuccessChartJson(1705276800, 30m),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(harness.Context, handler);

        var result = await service.RefreshHistoryAsync(target);

        Assert.Equal(5, handler.CallCount);
        Assert.Equal(0, result.DeletedPoints);
        Assert.Equal(5, result.ImportedPoints);

        await using var verificationContext = harness.CreateVerificationContext();
        var targetRows = await verificationContext.StockHistoricalPrices
            .Where(x => x.StockId == 1)
            .OrderBy(x => x.Interval)
            .ThenBy(x => x.Timestamp)
            .ToListAsync();
        Assert.Equal(6, targetRows.Count);
        Assert.Equal(1, await verificationContext.StockHistoricalPrices.CountAsync(x => x.StockId == 2));
    }

    [Fact]
    public async Task RefreshHistoryAsync_ProviderFailurePreservesExistingHistory()
    {
        await using var harness = await CreateSqliteHarnessAsync();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        harness.Context.Stocks.Add(stock);
        harness.Context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = 1,
            Interval = "1d",
            Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Open = 7m,
            High = 7m,
            Low = 7m,
            Close = 7m,
            QuoteUnitMultiplier = 1m
        });
        await harness.Context.SaveChangesAsync();

        var handler = new FailingHandler(new HttpRequestException("provider failed"));
        var service = CreateService(harness.Context, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshHistoryAsync(stock));

        await using var verificationContext = harness.CreateVerificationContext();
        var rows = await verificationContext.StockHistoricalPrices.Where(x => x.StockId == 1).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(7m, rows[0].Close);
    }

    [Fact]
    public async Task RefreshHistoryAsync_RateLimited_PreservesExistingHistoryAndStopsAfterFirstCall()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = 1,
            Interval = "1d",
            Timestamp = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Open = 7m,
            High = 7m,
            Low = 7m,
            Close = 7m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new StatusSequenceHandler(HttpStatusCode.TooManyRequests);
        var service = CreateService(context, handler);

        var result = await service.RefreshHistoryAsync(stock);

        Assert.Equal(0, result.DeletedPoints);
        Assert.Equal(0, result.ImportedPoints);
        Assert.Equal(1, handler.CallCount);

        var rows = await context.StockHistoricalPrices.Where(x => x.StockId == 1).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(7m, rows[0].Close);
    }

    [Fact]
    public async Task SyncHistoricalDataForAllStocksAsync_RateLimited_StopsFurtherFanOut()
    {
        await using var context = CreateInMemoryContext();
        context.Stocks.AddRange(
            new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" },
            new Stock { Id = 2, Ticker = "MSFT", Exchange = StockExchanges.Nyse, Name = "Microsoft" });
        await context.SaveChangesAsync();

        var handler = new StatusSequenceHandler(HttpStatusCode.TooManyRequests);
        var service = CreateService(context, handler);

        await service.SyncHistoricalDataForAllStocksAsync();

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SyncHistoricalDataForAllStocksAsync_ProcessesTrackedAndCatalogByCadence()
    {
        await using var context = CreateInMemoryContext();
        context.Stocks.AddRange(
            new Stock { Id = 1, Ticker = "TRACK", Exchange = StockExchanges.Nyse, Name = "Tracked", TrackingStatus = StockTrackingStatus.Tracked },
            new Stock { Id = 2, Ticker = "CATONLY", Exchange = StockExchanges.Nyse, Name = "Catalog", TrackingStatus = StockTrackingStatus.CatalogOnly });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(1704067200, 10m),
            SuccessChartJson(1704672000, 20m),
            SuccessChartJson(1705276800, 30m),
            SuccessChartJson(1705881600, 40m),
            SuccessChartJson(1706486400, 50m));
        var service = CreateService(context, handler);

        await service.SyncHistoricalDataForAllStocksAsync();

        Assert.Equal(6, handler.CallCount);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("TRACK", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("CATONLY", StringComparison.Ordinal));
        Assert.True(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == 2 && x.Interval == "1d"));
    }

    [Fact]
    public async Task SyncHistoricalDataForAllStocksAsync_UsesTierPrecedence_AndSingleProviderRequestPerStock()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AAPL",
            Exchange = StockExchanges.Nyse,
            Name = "Apple",
            TrackingStatus = StockTrackingStatus.Tracked,
            HistoryRefreshCadence = StockHistoryRefreshCadence.Daily,
            NextIncrementalHistoryRefreshAtUtc = now.AddDays(-1),
            NextHistoryReconciliationAtUtc = now.AddDays(-1),
            NextFullHistoryBackfillAtUtc = now.AddDays(-1),
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = now.AddDays(-2),
            Open = 1m,
            High = 1m,
            Low = 1m,
            Close = 1m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(SuccessChartJson(ToUnix(now.AddDays(-1)), 10m));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        await service.SyncHistoricalDataForAllStocksAsync();

        Assert.Equal(3, handler.CallCount);
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=1d", StringComparison.Ordinal) && url.Contains("period1=", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=1h", StringComparison.Ordinal));
        Assert.Contains(handler.RequestedUrls, url => url.Contains("interval=5m", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshHistoryAsync_AutomaticNewStock_UsesFullBackfillAndRequestsAtLeastFiveYears()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 90,
            Ticker = "WPM",
            Exchange = StockExchanges.Nyse,
            Name = "WPM"
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 6, 29, 20, 0, 0, DateTimeKind.Utc)), 100m, 100L),
                (ToUnix(new DateTime(2026, 7, 15, 20, 0, 0, DateTimeKind.Utc)), 101m, 101L),
                (ToUnix(new DateTime(2026, 7, 31, 20, 0, 0, DateTimeKind.Utc)), 102m, 102L),
                (ToUnix(new DateTime(2026, 8, 20, 20, 0, 0, DateTimeKind.Utc)), 103m, 103L)),
            SuccessChartJson((ToUnix(now.AddHours(-2)), 110m, 1000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-10)), 111m, 500L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var refresh = await service.RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic);

        Assert.Equal(nameof(StockHistoryRefreshTier.FullBackfill), refresh.AppliedTier);

        var dailyUrl = handler.RequestedUrls.Single(url => url.Contains("interval=1d", StringComparison.Ordinal) && url.Contains("period1=", StringComparison.Ordinal));
        var period1 = long.Parse(GetQueryParam(dailyUrl, "period1"), System.Globalization.CultureInfo.InvariantCulture);
        var period2 = long.Parse(GetQueryParam(dailyUrl, "period2"), System.Globalization.CultureInfo.InvariantCulture);
        var lookbackDays = TimeSpan.FromSeconds(period2 - period1).TotalDays;
        Assert.True(lookbackDays >= 1825d, $"Expected >= 1825 days but got {lookbackDays}.");
    }

    [Theory]
    [InlineData(StockExchanges.Nyse)]
    [InlineData(StockExchanges.Nasdaq)]
    [InlineData(StockExchanges.Frankfurt)]
    public async Task RefreshHistoryAsync_FullBackfill_PersistsWeeklyAndMonthlyAggregatesForAllSupportedExchanges(string exchange)
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 91,
            Ticker = "WPM",
            Exchange = exchange,
            Name = "WPM"
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 6, 29, 20, 0, 0, DateTimeKind.Utc)), 100m, 100L),
                (ToUnix(new DateTime(2026, 7, 1, 20, 0, 0, DateTimeKind.Utc)), 101m, 101L),
                (ToUnix(new DateTime(2026, 7, 15, 20, 0, 0, DateTimeKind.Utc)), 102m, 102L),
                (ToUnix(new DateTime(2026, 7, 31, 20, 0, 0, DateTimeKind.Utc)), 103m, 103L),
                (ToUnix(new DateTime(2026, 8, 18, 20, 0, 0, DateTimeKind.Utc)), 104m, 104L)),
            SuccessChartJson((ToUnix(now.AddHours(-2)), 110m, 1000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-10)), 111m, 500L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var refresh = await service.RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic);

        Assert.False(refresh.RateLimited);
        Assert.True(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.Interval == "1wk"));
        Assert.True(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.Interval == "1mo"));
    }

    [Fact]
    public async Task SyncHistoricalDataForAllStocksAsync_NotDueStock_SkipsProviderCall()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AAPL",
            Exchange = StockExchanges.Nyse,
            Name = "Apple",
            TrackingStatus = StockTrackingStatus.Tracked,
            HistoryRefreshCadence = StockHistoryRefreshCadence.Daily,
            NextIncrementalHistoryRefreshAtUtc = now.AddDays(1),
            NextHistoryReconciliationAtUtc = now.AddDays(2),
            NextFullHistoryBackfillAtUtc = now.AddDays(3),
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = now.AddDays(-2),
            Open = 1m,
            High = 1m,
            Low = 1m,
            Close = 1m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(SuccessChartJson(ToUnix(now.AddDays(-1)), 10m));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        await service.SyncHistoricalDataForAllStocksAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SyncHistoricalDataForAllStocksAsync_DisabledCadence_SkipsProviderCall()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        context.Stocks.Add(new Stock
        {
            Id = 1,
            Ticker = "AAPL",
            Exchange = StockExchanges.Nyse,
            Name = "Apple",
            TrackingStatus = StockTrackingStatus.Tracked,
            HistoryRefreshCadence = StockHistoryRefreshCadence.Disabled,
            NextIncrementalHistoryRefreshAtUtc = now.AddDays(-1),
            NextHistoryReconciliationAtUtc = now.AddDays(-1),
            NextFullHistoryBackfillAtUtc = now.AddDays(-1),
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(SuccessChartJson(ToUnix(now.AddDays(-1)), 10m));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        await service.SyncHistoricalDataForAllStocksAsync();

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RefreshHistoryAsync_AutomaticRateLimit_SetsTierRetryWithoutSuccessTimestamp()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 1,
            Ticker = "AAPL",
            Exchange = StockExchanges.Nyse,
            Name = "Apple",
            TrackingStatus = StockTrackingStatus.Tracked,
            HistoryRefreshCadence = StockHistoryRefreshCadence.Daily,
            NextFullHistoryBackfillAtUtc = now.AddMinutes(-10)
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = now.AddDays(-2),
            Open = 1m,
            High = 1m,
            Low = 1m,
            Close = 1m,
            QuoteUnitMultiplier = 1m
        });
        await context.SaveChangesAsync();

        var handler = new StatusSequenceHandler(HttpStatusCode.TooManyRequests);
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var result = await service.RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic);

        Assert.True(result.RateLimited);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == stock.Id);
        Assert.Null(persisted.LastFullHistoryBackfillSucceededAtUtc);
        Assert.Equal(now.AddHours(2), persisted.NextFullHistoryBackfillAtUtc);
    }

    [Fact]
    public async Task RefreshHistoryAsync_CancellationAndPerStockDuplicateCallProtectionRemainIntact()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(gate);
        var service = CreateService(context, handler);

        var firstCall = service.RefreshHistoryAsync(stock);
        await handler.WaitForFirstRequestAsync();

        using var cancellationTokenSource = new CancellationTokenSource();
        var secondCall = service.RefreshHistoryAsync(stock, cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => secondCall);

        gate.SetResult();
        await firstCall;
        Assert.Equal(5, handler.CallCount);
    }

    [Fact]
    public async Task GetHistoryAsync_RangeSelection_ContinuesToWork()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 24, 15, 30, 0, DateTimeKind.Utc);
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = 1,
            Interval = "10m",
            Timestamp = new DateTime(2026, 8, 24, 15, 20, 0, DateTimeKind.Utc),
            Open = 1m,
            High = 2m,
            Low = 1m,
            Close = 2m,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m,
            Volume = 100
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, new SequenceHandler(), new FixedTimeProvider(new DateTimeOffset(now)));
        var response = await service.GetHistoryAsync(stock, "today");

        Assert.Equal("today", response.Range);
        Assert.Equal("10m", response.Interval);
        Assert.Single(response.Points);
    }

    [Fact]
    public async Task RefreshHistoryAsync_PersistsYahooVolume_AndReturnsItFromHistory()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 24, 15, 30, 0, DateTimeKind.Utc);
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var intradayBase = new DateTimeOffset(now.AddMinutes(-30), TimeSpan.Zero);
        intradayBase = new DateTimeOffset(
            intradayBase.Year,
            intradayBase.Month,
            intradayBase.Day,
            intradayBase.Hour,
            (intradayBase.Minute / 10) * 10,
            0,
            TimeSpan.Zero);

        var handler = new SequenceHandler(
            SuccessChartJson(1704067200, 10m, 111),
            SuccessChartJson(1704672000, 20m, 222),
            SuccessChartJson(1705276800, 30m, 333),
            SuccessChartJson(1705881600, 40m, 444),
            SuccessChartJson(
                (intradayBase.ToUnixTimeSeconds(), 50m, 40L),
                (intradayBase.AddMinutes(5).ToUnixTimeSeconds(), 55m, 60L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        await service.RefreshHistoryAsync(stock);

        var monthlyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == 1 && x.Interval == "1mo");
        var dailyRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == 1 && x.Interval == "1d");
        var intradayRow = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == 1 && x.Interval == "10m");

        Assert.Equal(111, monthlyRow.Volume);
        Assert.Equal(333, dailyRow.Volume);
        Assert.Equal(100, intradayRow.Volume);

        var todayHistory = await service.GetHistoryAsync(stock, "today");
        var todayPoint = Assert.Single(todayHistory.Points);
        Assert.Equal(100, todayPoint.Volume);
    }

    [Fact]
    public async Task RefreshHistoryAsync_Frankfurt_UsesDailyAggregationForWeeklyVolumes_AndExcludesCurrentWeekMetrics()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc); // Wednesday
        var stock = new Stock { Id = 100, Ticker = "AMZN", Exchange = StockExchanges.Frankfurt, Name = "Amazon FRA" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new SequenceHandler(
            // Yahoo monthly/weekly payloads contain a collapsed low-volume tail.
            SuccessChartJson((ToUnix(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)), 100m, 120_000L)),
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc)), 101m, 50_000L),
                (ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 102m, 82L)),
            // Daily candles are the ground truth for weekly/monthly aggregation.
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc)), 100m, 10_000L),
                (ToUnix(new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc)), 101m, 11_000L),
                (ToUnix(new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc)), 102m, 9_000L),
                (ToUnix(new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc)), 103m, 10_000L),
                (ToUnix(new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc)), 104m, 10_000L),
                (ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 105m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)), 106m, 11_000L),
                (ToUnix(new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc)), 107m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)), 108m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)), 109m, 12_000L),
                // Current in-progress week must be excluded from weekly chart/metrics.
                (ToUnix(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc)), 110m, 82L)),
            SuccessChartJson((ToUnix(now.AddHours(-2)), 111m, 1_000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-10)), 112m, 500L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        await service.RefreshHistoryAsync(stock);

        var weeklyRows = await context.StockHistoricalPrices
            .Where(x => x.StockId == stock.Id && x.Interval == "1wk")
            .OrderBy(x => x.Timestamp)
            .ToListAsync();

        Assert.Equal(2, weeklyRows.Count);
        Assert.Equal(50_000L, weeklyRows[0].Volume);
        Assert.Equal(60_000L, weeklyRows[1].Volume);
        Assert.DoesNotContain(weeklyRows, row => row.Volume == 82L);

        var history = await service.GetHistoryAsync(stock, "1y");
        var lastPoint = Assert.Single(history.Points, p => p.Timestamp == weeklyRows[^1].Timestamp);
        Assert.Equal(60_000L, lastPoint.Volume);
        Assert.True(history.VolumeMetrics.UsesCompletedCandle);
        Assert.Equal(weeklyRows[^1].Timestamp, history.VolumeMetrics.LatestMetricsTimestamp);
    }

    [Fact]
    public async Task RefreshHistoryAsync_AutomaticFrankfurtRefresh_RebuildsLongRangeWithoutManualReset()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        var stock = new Stock
        {
            Id = 101,
            Ticker = "AMZN",
            Exchange = StockExchanges.Frankfurt,
            Name = "Amazon FRA",
            HistoryRefreshCadence = StockHistoryRefreshCadence.Daily,
            NextIncrementalHistoryRefreshAtUtc = now.AddDays(-1),
            NextHistoryReconciliationAtUtc = now.AddDays(10),
            NextFullHistoryBackfillAtUtc = now.AddDays(10)
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "1d",
                Timestamp = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc),
                Open = 100m,
                High = 100m,
                Low = 100m,
                Close = 100m,
                QuoteCurrency = "EUR",
                FinancialCurrency = "EUR",
                NormalizedQuoteCurrency = "EUR",
                QuoteUnitMultiplier = 1m,
                Volume = 10_000L
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "1wk",
                Timestamp = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
                Open = 100m,
                High = 100m,
                Low = 100m,
                Close = 100m,
                QuoteCurrency = "EUR",
                FinancialCurrency = "EUR",
                NormalizedQuoteCurrency = "EUR",
                QuoteUnitMultiplier = 1m,
                Volume = 82L
            });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 101m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)), 102m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc)), 103m, 11_000L),
                (ToUnix(new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)), 104m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)), 105m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc)), 106m, 82L)),
            SuccessChartJson((ToUnix(now.AddHours(-1)), 110m, 1_000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-5)), 111m, 500L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var refresh = await service.RefreshHistoryAsync(stock, StockHistoryRefreshTrigger.Automatic);

        Assert.False(refresh.RateLimited);
        Assert.Equal(3, handler.CallCount);

        var weeklyRows = await context.StockHistoricalPrices
            .Where(x => x.StockId == stock.Id && x.Interval == "1wk")
            .OrderBy(x => x.Timestamp)
            .ToListAsync();
        Assert.DoesNotContain(weeklyRows, row => row.Volume == 82L);
        Assert.Contains(weeklyRows, row => row.Volume == 60_000L);
    }

    [Fact]
    public async Task GetHistoryAsync_ComputesVolumeMetrics_FromSelectedListingHistory()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);

        var startDate = DateTime.UtcNow.Date.AddDays(-50);
        for (var i = 0; i < 50; i++)
        {
            context.StockHistoricalPrices.Add(new StockHistoricalPrice
            {
                StockId = 1,
                Interval = "1d",
                Timestamp = startDate.AddDays(i),
                Open = 100m + i,
                High = 100m + i,
                Low = 100m + i,
                Close = 100m + i,
                QuoteCurrency = "USD",
                FinancialCurrency = "USD",
                NormalizedQuoteCurrency = "USD",
                QuoteUnitMultiplier = 1m,
                Volume = i + 1
            });
        }

        await context.SaveChangesAsync();

        var service = CreateService(context, new SequenceHandler());
        var response = await service.GetHistoryAsync(stock, "6m");

        Assert.Equal(40.5m, response.VolumeMetrics.AverageVolume20);
        Assert.Equal(25.5m, response.VolumeMetrics.AverageVolume50);
        Assert.Equal(50m / 40.5m, response.VolumeMetrics.RelativeVolume);
        Assert.Equal(149m * 50m, response.VolumeMetrics.Turnover);
        Assert.Equal("EUR", response.VolumeMetrics.TurnoverCurrency);
        Assert.True(response.VolumeMetrics.UsesCompletedCandle);
        Assert.Equal(startDate.AddDays(49), response.VolumeMetrics.LatestMetricsTimestamp);
    }

    [Fact]
    public async Task GetHistoryAsync_WithInsufficientPeriods_ReturnsNullAverageMetrics()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);

        var startDate = DateTime.UtcNow.Date.AddDays(-19);
        for (var i = 0; i < 19; i++)
        {
            context.StockHistoricalPrices.Add(new StockHistoricalPrice
            {
                StockId = 1,
                Interval = "1d",
                Timestamp = startDate.AddDays(i),
                Open = 20m + i,
                High = 20m + i,
                Low = 20m + i,
                Close = 20m + i,
                QuoteCurrency = "USD",
                FinancialCurrency = "USD",
                NormalizedQuoteCurrency = "USD",
                QuoteUnitMultiplier = 1m,
                Volume = 100 + i
            });
        }

        await context.SaveChangesAsync();

        var service = CreateService(context, new SequenceHandler());
        var response = await service.GetHistoryAsync(stock, "6m");

        Assert.Null(response.VolumeMetrics.AverageVolume20);
        Assert.Null(response.VolumeMetrics.AverageVolume50);
        Assert.Null(response.VolumeMetrics.RelativeVolume);
        Assert.Equal(38m * 118m, response.VolumeMetrics.Turnover);
    }

    [Fact]
    public async Task GetHistoryAsync_WithZeroVolumeBaseline_AvoidsZeroAverageAndRelativeVolume()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 1, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);

        var startDate = DateTime.UtcNow.Date.AddDays(-20);
        for (var i = 0; i < 20; i++)
        {
            context.StockHistoricalPrices.Add(new StockHistoricalPrice
            {
                StockId = 1,
                Interval = "1d",
                Timestamp = startDate.AddDays(i),
                Open = 10m,
                High = 10m,
                Low = 10m,
                Close = 10m,
                QuoteCurrency = "USD",
                FinancialCurrency = "USD",
                NormalizedQuoteCurrency = "USD",
                QuoteUnitMultiplier = 1m,
                Volume = 0
            });
        }

        await context.SaveChangesAsync();

        var service = CreateService(context, new SequenceHandler());
        var response = await service.GetHistoryAsync(stock, "6m");

        Assert.Null(response.VolumeMetrics.AverageVolume20);
        Assert.Null(response.VolumeMetrics.AverageVolume50);
        Assert.Null(response.VolumeMetrics.RelativeVolume);
        Assert.Null(response.VolumeMetrics.Turnover);
    }

    [Fact]
    public async Task HardResetHistoryAsync_Success_DeletesAllRowsAcrossIntervals_AndPersistsProviderOverride()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock
        {
            Id = 7,
            Ticker = "SMEGF",
            Exchange = StockExchanges.Frankfurt,
            Name = "Siemens Energy AG",
            ProviderSymbol = "SMEGF",
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "10m",
                Timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m,
                IsQuoteDerived = true,
            },
            new StockHistoricalPrice
            {
                StockId = stock.Id,
                Interval = "1d",
                Timestamp = new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                Open = 2m, High = 2m, Low = 2m, Close = 2m, Volume = 2,
                QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m,
            });
        await context.SaveChangesAsync();

        var now = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CountingHandler(
            SuccessChartJson(BuildCandles(ToUnix(now.AddMonths(-18)), 12, TimeSpan.FromDays(30), 10m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddDays(-220)), 30, TimeSpan.FromDays(7), 20m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddDays(-600)), 600, TimeSpan.FromDays(1), 30m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddDays(-2)), 8, TimeSpan.FromHours(1), 40m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddHours(-8)), 12, TimeSpan.FromMinutes(5), 50m)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var result = await service.HardResetHistoryAsync(stock, "ENR.DE");

        Assert.True(result.ResetPerformed);
        Assert.Equal("success", result.ResultBucket);
        Assert.Equal("ENR.DE", await context.Stocks.Where(x => x.Id == stock.Id).Select(x => x.ProviderSymbol).SingleAsync());
        Assert.True(result.DeletedRows >= 2);
        Assert.Equal(result.InsertedRows, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
        Assert.False(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.IsQuoteDerived));
        Assert.False(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.Timestamp.Year == 2000));
    }

    [Fact]
    public async Task HardResetHistoryAsync_ValidationFailure_DoesNotDeleteExistingHistory()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 8, Ticker = "TEST", Exchange = StockExchanges.Nyse, Name = "Test" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Open = 10m, High = 10m, Low = 10m, Close = 10m, Volume = 10,
            QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m,
        });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson(1704067200, 10m), // 1mo insufficient
            SuccessChartJson(1704067200, 10m), // 1wk insufficient
            SuccessChartJson(1704067200, 10m), // 1d insufficient
            SuccessChartJson(1704067200, 10m), // 1h
            SuccessChartJson(1704067200, 10m)); // 5m
        var service = CreateService(context, handler);

        var beforeCount = await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id);
        var result = await service.HardResetHistoryAsync(stock, null);
        var afterCount = await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id);

        Assert.False(result.ResetPerformed);
        Assert.Equal("validationFailed", result.ResultBucket);
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public async Task HardResetHistoryAsync_WhenIntradayUnavailable_RemovesOldIntradayAndReturnsPartial()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 9, Ticker = "TEST2", Exchange = StockExchanges.Nyse, Name = "Test2" };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "10m",
            Timestamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            Open = 20m, High = 20m, Low = 20m, Close = 20m, Volume = 20,
            QuoteCurrency = "USD", FinancialCurrency = "USD", NormalizedQuoteCurrency = "USD", QuoteUnitMultiplier = 1m,
            IsQuoteDerived = true,
        });
        await context.SaveChangesAsync();

        var now = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);
        var handler = new CountingHandler(
            SuccessChartJson(BuildCandles(ToUnix(now.AddMonths(-18)), 12, TimeSpan.FromDays(30), 10m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddDays(-220)), 30, TimeSpan.FromDays(7), 20m)),
            SuccessChartJson(BuildCandles(ToUnix(now.AddDays(-160)), 140, TimeSpan.FromDays(1), 30m)),
            """{"chart":{"result":[]}}""",
            """{"chart":{"result":[]}}""");
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var result = await service.HardResetHistoryAsync(stock, null);

        Assert.True(result.ResetPerformed);
        Assert.Equal("partial", result.ResultBucket);
        Assert.Contains(result.Intervals, x => x.Interval == "10m" && x.IsUnavailable);
        Assert.False(await context.StockHistoricalPrices.AnyAsync(x => x.StockId == stock.Id && x.Interval == "10m"));
    }

    [Fact]
    public async Task ValidateProviderSymbolAsync_InvalidCandidate_IsRejectedBeforeProviderCalls()
    {
        await using var context = CreateInMemoryContext();
        var stock = new Stock { Id = 10, Ticker = "AAPL", Exchange = StockExchanges.Nyse, Name = "Apple" };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var handler = new CountingHandler();
        var service = CreateService(context, handler);

        var result = await service.ValidateProviderSymbolAsync(stock, "bad symbol with space");

        Assert.Equal("validationFailed", result.ResultBucket);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RebuildFrankfurtAggregatesAsync_BatchesAndSupportsCursorResume()
    {
        await using var context = CreateInMemoryContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        context.Stocks.AddRange(
            new Stock { Id = 201, Ticker = "AMZN", Exchange = StockExchanges.Frankfurt, Name = "Amazon FRA" },
            new Stock { Id = 202, Ticker = "RHM.DE", Exchange = StockExchanges.Frankfurt, Name = "Rheinmetall" },
            new Stock { Id = 203, Ticker = "AAPL", Exchange = StockExchanges.Nasdaq, Name = "Apple" });
        await context.SaveChangesAsync();

        var handler = new CountingHandler(
            SuccessChartJson((ToUnix(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)), 100m, 100_000L)),
            SuccessChartJson((ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 101m, 82L)),
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 101m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)), 102m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc)), 103m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)), 104m, 12_000L),
                (ToUnix(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)), 105m, 12_000L)),
            SuccessChartJson((ToUnix(now.AddHours(-1)), 110m, 1_000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-5)), 111m, 500L)),
            SuccessChartJson((ToUnix(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)), 200m, 100_000L)),
            SuccessChartJson((ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 201m, 90L)),
            SuccessChartJson(
                (ToUnix(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc)), 201m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc)), 202m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc)), 203m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)), 204m, 13_000L),
                (ToUnix(new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc)), 205m, 13_000L)),
            SuccessChartJson((ToUnix(now.AddHours(-1)), 210m, 1_000L)),
            SuccessChartJson((ToUnix(now.AddMinutes(-5)), 211m, 500L)));
        var service = CreateService(context, handler, new FixedTimeProvider(new DateTimeOffset(now)));

        var firstBatch = await service.RebuildFrankfurtAggregatesAsync(batchSize: 1, afterStockId: null);
        Assert.Equal(1, firstBatch.RebuiltStocks);
        Assert.True(firstBatch.HasMore);
        Assert.Equal(201, firstBatch.NextAfterStockId);

        var secondBatch = await service.RebuildFrankfurtAggregatesAsync(batchSize: 10, afterStockId: firstBatch.NextAfterStockId);
        Assert.Equal(1, secondBatch.RebuiltStocks);
        Assert.False(secondBatch.HasMore);
        Assert.Equal(202, secondBatch.NextAfterStockId);

        var finished = await service.RebuildFrankfurtAggregatesAsync(batchSize: 10, afterStockId: secondBatch.NextAfterStockId);
        Assert.Equal(0, finished.RebuiltStocks);
        Assert.False(finished.HasMore);
        Assert.Equal(10, handler.CallCount);
    }

    private static AppDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static (long Timestamp, decimal Close, long Volume)[] BuildCandles(
        long startUnixSeconds,
        int count,
        TimeSpan step,
        decimal startClose)
    {
        return Enumerable.Range(0, count)
            .Select(i => (
                Timestamp: startUnixSeconds + (long)(step.TotalSeconds * i),
                Close: startClose + i,
                Volume: 100L + i))
            .ToArray();
    }

    private static StockHistoricalPrice CreateDailyRow(
        int stockId,
        DateTime timestamp,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        decimal? adjustedClose = null,
        bool isQuoteDerived = false)
        => new()
        {
            StockId = stockId,
            Interval = "1d",
            Timestamp = timestamp,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            AdjustedClose = adjustedClose,
            QuoteCurrency = "USD",
            FinancialCurrency = "USD",
            NormalizedQuoteCurrency = "USD",
            QuoteUnitMultiplier = 1m,
            Volume = volume,
            IsQuoteDerived = isQuoteDerived
        };

    private static string GetQueryParam(string url, string key)
    {
        var uri = new Uri(url);
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty, StringComparer.Ordinal);
        return query[key];
    }

    private static async Task<SqliteHarness> CreateSqliteHarnessAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var context = CreateSqliteContext(connection);
        await context.Database.EnsureCreatedAsync();

        return new SqliteHarness(connection, context);
    }

    private static AppDbContext CreateSqliteContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        return new AppDbContext(options);
    }

    private static StockHistoryService CreateService(
        AppDbContext context,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        StockHistoryRefreshOptions? refreshOptions = null)
    {
        var coordinator = new YahooRequestCoordinator(
            new FixedHttpClientFactory(new HttpClient(handler)),
            NullLogger<YahooRequestCoordinator>.Instance,
            Options.Create(new YahooFinanceOptions
            {
                MinRequestInterval = TimeSpan.Zero,
                CooldownDuration = TimeSpan.FromMinutes(30),
                QuoteCacheDuration = TimeSpan.Zero,
                RequestTimeout = TimeSpan.FromSeconds(10)
            }));
        return new StockHistoryService(
            context,
            coordinator,
            new StubStockQuoteConversionService(),
            timeProvider ?? TimeProvider.System,
            Options.Create(refreshOptions ?? new StockHistoryRefreshOptions()),
            NullLogger<StockHistoryService>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static string SuccessChartJson(long unixTimestamp, decimal close, long volume = 100L) =>
        SuccessChartJson((unixTimestamp, close, volume));

    private static string SuccessChartJson(params (long Timestamp, decimal Close, long Volume)[] candles)
        => SuccessChartJson(candles.Select(x => (x.Timestamp, x.Close, x.Volume, (decimal?)null)).ToArray());

    private static string SuccessChartJson(params (long Timestamp, decimal Close, long Volume, decimal? AdjustedClose)[] candles)
    {
        var timestamps = string.Join(",", candles.Select(x => x.Timestamp));
        var opens = string.Join(",", candles.Select(x => x.Close));
        var highs = string.Join(",", candles.Select(x => x.Close));
        var lows = string.Join(",", candles.Select(x => x.Close));
        var closes = string.Join(",", candles.Select(x => x.Close));
        var volumes = string.Join(",", candles.Select(x => x.Volume));
        var hasAdjustedClose = candles.Any(x => x.AdjustedClose.HasValue);
        var adjustedCloseSection = hasAdjustedClose
            ? $@",""adjclose"":[{{""adjclose"":[{string.Join(",", candles.Select(x => x.AdjustedClose?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"))}]}}]"
            : string.Empty;
        return $@"{{""chart"":{{""result"":[{{""meta"":{{""currency"":""USD"",""financialCurrency"":""USD""}},""timestamp"":[{timestamps}],""indicators"":{{""quote"":[{{""open"":[{opens}],""high"":[{highs}],""low"":[{lows}],""close"":[{closes}],""volume"":[{volumes}]}}]{adjustedCloseSection}}}}}]}}}}";
    }

    private static string RawChartJson(
        string timestamps,
        string opens,
        string highs,
        string lows,
        string closes,
        string volumes,
        string? adjcloseArray)
    {
        var adjcloseSection = adjcloseArray is null
            ? string.Empty
            : $@",""adjclose"":[{{{adjcloseArray}}}]";
        return $@"{{""chart"":{{""result"":[{{""meta"":{{""currency"":""USD"",""financialCurrency"":""USD""}},""timestamp"":[{timestamps}],""indicators"":{{""quote"":[{{""open"":[{opens}],""high"":[{highs}],""low"":[{lows}],""close"":[{closes}],""volume"":[{volumes}]}}]{adjcloseSection}}}}}]}}}}";
    }

    private static string EmptyChartJson() => """{"chart":{"result":[]}}""";

    private static long ToUnix(DateTime value) => new DateTimeOffset(value).ToUnixTimeSeconds();

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FixedHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        private readonly List<string> _requestedUrls = new();

        public SequenceHandler(params string[] responses) => _responses = new Queue<string>(responses);
        public IReadOnlyList<string> RequestedUrls => _requestedUrls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            var body = _responses.Count > 0 ? _responses.Dequeue() : EmptyChartJson();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CountingHandler : SequenceHandler
    {
        private int _callCount;

        public CountingHandler(params string[] responses)
            : base(responses)
        {
        }

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FailingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StatusSequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(EmptyChartJson(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class BlockingHandler(TaskCompletionSource gate) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => _callCount;

        public Task WaitForFirstRequestAsync() => _firstRequest.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _firstRequest.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessChartJson(1704067200 + call, 10m + call), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubStockQuoteConversionService : IStockQuoteConversionService
    {
        public Task<CurrencyConversionContext> GetConversionContextAsync(string? quoteCurrency, string? financialCurrency, CancellationToken cancellationToken = default)
        {
            var meta = QuoteCurrencyMetadata.Parse(quoteCurrency, financialCurrency);
            var rate = new ExchangeRateResult(quoteCurrency, 1m, DateTime.UtcNow, "stub", null);
            return Task.FromResult(new CurrencyConversionContext(meta, rate, null));
        }

        public StockQuoteResponse BuildQuoteResponse(string symbol, decimal rawCurrentPrice, decimal rawPreviousClose, decimal percentChange, string marketState, CurrencyConversionContext conversionContext, string priceSession = "REGULAR", DateTime? priceTimestampUtc = null, string? priceSource = null, string? delayWarning = null, decimal? rawDayHigh = null, decimal? rawDayLow = null)
            => new() { Symbol = symbol };

        public StockHistoryPointResponse BuildHistoryPointResponse(StockHistoricalPrice historicalPrice, CurrencyConversionContext conversionContext)
            => new()
            {
                Timestamp = historicalPrice.Timestamp,
                Interval = historicalPrice.Interval,
                OpenRaw = historicalPrice.Open,
                HighRaw = historicalPrice.High,
                LowRaw = historicalPrice.Low,
                CloseRaw = historicalPrice.Close,
                OpenNormalized = historicalPrice.Open,
                HighNormalized = historicalPrice.High,
                LowNormalized = historicalPrice.Low,
                CloseNormalized = historicalPrice.Close,
                OpenEur = historicalPrice.Open,
                HighEur = historicalPrice.High,
                LowEur = historicalPrice.Low,
                CloseEur = historicalPrice.Close,
                Volume = historicalPrice.Volume
            };
    }

    private sealed class SqliteHarness(
        SqliteConnection connection,
        AppDbContext context) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public AppDbContext Context { get; } = context;

        public AppDbContext CreateVerificationContext() =>
            CreateSqliteContext(Connection);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

}
