using System.Security.Claims;
using FinanceApp.API.Controllers;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FinanceApp.Core.Tests;

/// <summary>
/// Tests for CatalogOnly vs Tracked stock architecture.
/// </summary>
public class StockTrackingStatusTests
{
    // ── GET /api/stocks ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_DefaultRequest_ReturnsOnlyTrackedStocks()
    {
        await using var context = CreateContext();
        context.Stocks.AddRange(
            new Stock { Ticker = "AAPL", Name = "Apple", CommonName = "Apple", Exchange = StockExchanges.Nyse, CurrentPrice = 100m, TrackingStatus = StockTrackingStatus.Tracked },
            new Stock { Ticker = "MSFT", Name = "Microsoft", CommonName = "Microsoft", Exchange = StockExchanges.Nyse, CurrentPrice = 200m, TrackingStatus = StockTrackingStatus.CatalogOnly });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetAll();

        var stocks = Assert.IsAssignableFrom<IEnumerable<Stock>>(
            Assert.IsType<ActionResult<IEnumerable<Stock>>>(result).Value);
        Assert.Single(stocks);
        Assert.All(stocks, s => Assert.Equal(StockTrackingStatus.Tracked, s.TrackingStatus));
    }

    [Fact]
    public async Task GetAll_IncludeCatalogTrue_ReturnsAllStocks()
    {
        await using var context = CreateContext();
        context.Stocks.AddRange(
            new Stock { Ticker = "AAPL", Name = "Apple", CommonName = "Apple", Exchange = StockExchanges.Nyse, CurrentPrice = 100m, TrackingStatus = StockTrackingStatus.Tracked },
            new Stock { Ticker = "MSFT", Name = "Microsoft", CommonName = "Microsoft", Exchange = StockExchanges.Nyse, CurrentPrice = 200m, TrackingStatus = StockTrackingStatus.CatalogOnly });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetAll(includeCatalog: true);

        var stocks = Assert.IsAssignableFrom<IEnumerable<Stock>>(
            Assert.IsType<ActionResult<IEnumerable<Stock>>>(result).Value);
        Assert.Equal(2, stocks.Count());
    }

    // ── POST /api/stocks — standard create always Tracked ────────────────────

    [Fact]
    public async Task Create_StandardRequest_CreatesTrackedStock()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "TSLA",
            Name = "Tesla Inc.",
            CommonName = "Tesla",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 300m,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);
        Assert.Equal(StockTrackingStatus.Tracked, stock.TrackingStatus);
    }

    [Fact]
    public async Task Create_CatalogOnlyInPayload_StillCreatesTrackedStock()
    {
        // Clients must not be able to create CatalogOnly stocks through the standard API.
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "TSLA",
            Name = "Tesla Inc.",
            CommonName = "Tesla",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 300m,
            TrackingStatus = StockTrackingStatus.CatalogOnly, // should be overridden
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);
        Assert.Equal(StockTrackingStatus.Tracked, stock.TrackingStatus);
    }

    // ── GET history / manual refresh allow CatalogOnly without promotion ─────

    [Fact]
    public async Task GetHistory_CatalogOnlyStock_ReturnsOkWithoutTrackingMutation()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetHistory(stock.Id, "1y");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(StockTrackingStatus.CatalogOnly, (await context.Stocks.FindAsync(stock.Id))!.TrackingStatus);
    }

    [Fact]
    public async Task RefreshHistory_CatalogOnlyStock_ReturnsOkWithoutTrackingMutation()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.RefreshHistory(stock.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockHistoryRefreshResponse>(ok.Value);
        Assert.Equal(stock.Id, payload.StockId);
        Assert.Equal(StockTrackingStatus.CatalogOnly, (await context.Stocks.FindAsync(stock.Id))!.TrackingStatus);
    }

    // ── POST /api/stocks/{id}/track ──────────────────────────────────────────

    [Fact]
    public async Task Track_CatalogOnlyStock_PromotesToTracked()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Track(stock.Id);

        var ok = Assert.IsType<ActionResult<Stock>>(result);
        var promoted = Assert.IsType<Stock>(ok.Value);
        Assert.Equal(StockTrackingStatus.Tracked, promoted.TrackingStatus);
        Assert.Equal(StockTrackingStatus.Tracked, (await context.Stocks.FindAsync(stock.Id))!.TrackingStatus);
    }

    [Fact]
    public async Task Track_AlreadyTrackedStock_Returns200WithoutDuplicate()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Track(stock.Id);

        var ok = Assert.IsType<ActionResult<Stock>>(result);
        Assert.Equal(StockTrackingStatus.Tracked, ok.Value!.TrackingStatus);
        Assert.Equal(1, await context.Stocks.CountAsync());
    }

    [Fact]
    public async Task Untrack_TrackedStock_KeepsStockHistoryFundamentalsMembershipsAndPortfolioReferences()
    {
        await using var context = CreateContext();

        var user = new User
        {
            Username = "user",
            Email = "user@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
        };
        var portfolio = new Portfolio
        {
            Name = "Main",
            User = user,
            CreatedAt = DateTime.UtcNow,
        };
        var index = new MarketIndex
        {
            Id = 1,
            Name = "S&P 500",
            NormalizedName = "S&P 500",
            Code = "SPX",
            NormalizedCode = "SPX",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { MarketIndex = index, EffectiveFrom = DateTime.UtcNow, ImportedAt = DateTime.UtcNow }
            }
        };

        context.Stocks.Add(stock);
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync();

        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Timestamp = DateTime.UtcNow.Date,
            Interval = "1d",
            Open = 100m,
            High = 101m,
            Low = 99m,
            Close = 100m,
            QuoteUnitMultiplier = 1m,
        });
        context.FundamentalsSnapshots.Add(new CompanyFundamentalsSnapshot
        {
            StockId = stock.Id,
            SourceSymbol = "AAPL",
            Source = "Test",
            FetchedAtUtc = DateTime.UtcNow,
        });
        context.PortfolioItems.Add(new PortfolioItem
        {
            PortfolioId = portfolio.Id,
            StockId = stock.Id,
            Quantity = 1m,
            BuyPrice = 100m,
            BoughtAt = DateTime.UtcNow,
        });
        context.Orders.Add(new Order
        {
            PortfolioId = portfolio.Id,
            StockId = stock.Id,
            Side = OrderSide.Buy,
            Quantity = 1m,
            Price = 100m,
            CreatedAt = DateTime.UtcNow,
        });
        context.Transactions.Add(new Transaction
        {
            PortfolioId = portfolio.Id,
            Type = TransactionType.Buy,
            Amount = 100m,
            SignedAmount = -100m,
            StockId = stock.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Untrack(stock.Id);

        var actionResult = Assert.IsType<ActionResult<Stock>>(result);
        Assert.Equal(StockTrackingStatus.CatalogOnly, actionResult.Value!.TrackingStatus);
        Assert.Equal(StockTrackingStatus.CatalogOnly, (await context.Stocks.FindAsync(stock.Id))!.TrackingStatus);
        Assert.Equal(1, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(1, await context.FundamentalsSnapshots.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == stock.Id && x.EffectiveTo == null));
        Assert.Equal(1, await context.PortfolioItems.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(1, await context.Orders.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(1, await context.Transactions.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task Untrack_StockAppearsInCatalogButNotTrackedAndRetrackReusesSameId()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "BASF",
            Name = "BASF",
            CommonName = "BASF",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 50m,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();
        var stockId = stock.Id;

        var controller = CreateController(context);
        await controller.Untrack(stockId);

        var tracked = Assert.IsAssignableFrom<IEnumerable<Stock>>((await controller.GetAll()).Value!).ToList();
        var catalog = Assert.IsAssignableFrom<IEnumerable<Stock>>((await controller.GetAll(includeCatalog: true)).Value!).ToList();
        Assert.DoesNotContain(tracked, x => x.Id == stockId);
        Assert.Contains(catalog, x => x.Id == stockId);

        await controller.Track(stockId);
        Assert.Equal(1, await context.Stocks.CountAsync());
        Assert.Equal(stockId, (await context.Stocks.SingleAsync()).Id);
        Assert.Equal(StockTrackingStatus.Tracked, (await context.Stocks.SingleAsync()).TrackingStatus);
    }

    [Fact]
    public async Task Untrack_Idempotent_AndWorksForStockWithoutIndexMembership()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "SOLO",
            Name = "Solo Corp",
            CommonName = "Solo Corp",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var first = await controller.Untrack(stock.Id);
        var second = await controller.Untrack(stock.Id);

        Assert.Equal(StockTrackingStatus.CatalogOnly, first.Value!.TrackingStatus);
        Assert.Equal(StockTrackingStatus.CatalogOnly, second.Value!.TrackingStatus);
        Assert.Equal(1, await context.Stocks.CountAsync(x => x.Id == stock.Id));
        var catalog = Assert.IsAssignableFrom<IEnumerable<Stock>>((await controller.GetAll(includeCatalog: true)).Value!);
        Assert.Contains(catalog, x => x.Id == stock.Id);
    }

    // ── DELETE demotes CatalogOnly when still in an index ────────────────────

    [Fact]
    public async Task Delete_TrackedStockStillInIndex_DemotesToCatalogOnly()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX",
            NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var stock = new Stock
        {
            Ticker = "AAPL", Name = "Apple", CommonName = "Apple",
            Exchange = StockExchanges.Nyse, CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { MarketIndexId = 1 }
            }
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Delete(stock.Id);

        Assert.IsType<NoContentResult>(result);
        // Stock must still exist but as CatalogOnly
        var remaining = await context.Stocks.FindAsync(stock.Id);
        Assert.NotNull(remaining);
        Assert.Equal(StockTrackingStatus.CatalogOnly, remaining!.TrackingStatus);
    }

    [Fact]
    public async Task Delete_TrackedStockNoIndexMembership_DeletesPhysically()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Ticker = "AAPL", Name = "Apple", CommonName = "Apple",
            Exchange = StockExchanges.Nyse, CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.Tracked,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Delete(stock.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await context.Stocks.FindAsync(stock.Id));
    }

    // ── Membership history: EffectiveTo semantics ─────────────────────────────

    [Fact]
    public async Task UpdateMetadata_RemoveIndexMembership_SetsEffectiveTo_NotPhysicalDelete()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX",
            NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var stock = new Stock
        {
            Ticker = "AAPL", Name = "Apple", CommonName = "Apple",
            Exchange = StockExchanges.Nyse, CurrentPrice = 100m,
            MarketIndices = new List<StockMarketIndex> { new() { MarketIndexId = 1 } }
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        await controller.UpdateMetadata(stock.Id, new UpdateStockMetadataRequest
        {
            Name = stock.Name,
            CommonName = stock.CommonName,
            CurrentPrice = stock.CurrentPrice,
            MarketIndexIds = new List<int>(),
        });

        // The row must still exist but with EffectiveTo set
        var allRows = await context.StockMarketIndices.Where(x => x.StockId == stock.Id).ToListAsync();
        Assert.Single(allRows);
        Assert.NotNull(allRows[0].EffectiveTo);

        // Current membership is empty
        var current = await context.StockMarketIndices.Where(x => x.StockId == stock.Id && x.EffectiveTo == null).ToListAsync();
        Assert.Empty(current);
    }

    // ── GetAll excludes catalog stocks from response ─────────────────────────

    [Fact]
    public async Task GetAll_CatalogOnlyStocksAreExcluded()
    {
        await using var context = CreateContext();
        context.Stocks.AddRange(
            new Stock { Ticker = "A", Name = "Alpha", CommonName = "Alpha", Exchange = StockExchanges.Nyse, CurrentPrice = 1m, TrackingStatus = StockTrackingStatus.Tracked },
            new Stock { Ticker = "B", Name = "Beta", CommonName = "Beta", Exchange = StockExchanges.Nyse, CurrentPrice = 2m, TrackingStatus = StockTrackingStatus.CatalogOnly },
            new Stock { Ticker = "C", Name = "Gamma", CommonName = "Gamma", Exchange = StockExchanges.Nyse, CurrentPrice = 3m, TrackingStatus = StockTrackingStatus.CatalogOnly });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetAll();
        var stocks = Assert.IsAssignableFrom<IEnumerable<Stock>>(
            Assert.IsType<ActionResult<IEnumerable<Stock>>>(result).Value).ToList();

        Assert.Single(stocks);
        Assert.Equal("A", stocks[0].Ticker);
    }

    // ── Constituents refresh: unsupported provider ────────────────────────────

    [Fact]
    public async Task RefreshConstituents_UnsupportedProvider_Returns422()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX",
            NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = CreateMarketIndicesController(context);
        var result = await controller.RefreshConstituents(1);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, (result.Result as ObjectResult)?.StatusCode);
        var body = Assert.IsType<IndexConstituentsRefreshResponse>((result.Result as ObjectResult)?.Value);
        Assert.Equal("Unsupported", body.ProviderStatus);
    }

    [Fact]
    public async Task RefreshConstituents_FirstImport_CreatesCatalogOnlyMemberships_WithoutHistoryOrFundamentals()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "Dow Jones", NormalizedName = "DOW JONES", Code = "DJIA",
            NormalizedCode = "DJIA", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, null),
            new IndexConstituentEntry("MSFT", "MSFT", "Microsoft Corporation", StockExchanges.Nasdaq, null),
        });

        var controller = CreateMarketIndicesController(context, provider);
        var result = await controller.RefreshConstituents(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);

        Assert.Equal("Success", body.ProviderStatus);
        Assert.Equal(2, body.Added);
        Assert.Equal(0, body.Closed);
        Assert.Equal(0, body.Conflicts);
        Assert.Equal(2, await context.Stocks.CountAsync());
        Assert.All(await context.Stocks.ToListAsync(), s => Assert.Equal(StockTrackingStatus.CatalogOnly, s.TrackingStatus));
        Assert.Equal(2, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 1 && x.EffectiveTo == null));
        Assert.Equal(0, await context.StockHistoricalPrices.CountAsync());
        Assert.Equal(0, await context.FundamentalsSnapshots.CountAsync());
    }

    [Fact]
    public async Task RefreshConstituents_RepeatedImport_IsIdempotent()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "Dow Jones", NormalizedName = "DOW JONES", Code = "DJIA",
            NormalizedCode = "DJIA", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var snapshot = new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, null),
            new IndexConstituentEntry("MSFT", "MSFT", "Microsoft Corporation", StockExchanges.Nasdaq, null),
        };
        var provider = new SnapshotProvider(snapshot);
        var controller = CreateMarketIndicesController(context, provider);

        await controller.RefreshConstituents(1);
        var result2 = await controller.RefreshConstituents(1);
        var ok2 = Assert.IsType<OkObjectResult>(result2.Result);
        var body2 = Assert.IsType<IndexConstituentsRefreshResponse>(ok2.Value);

        Assert.Equal(2, await context.Stocks.CountAsync());
        Assert.Equal(2, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 1 && x.EffectiveTo == null));
        Assert.Equal(0, body2.Added);
        Assert.Equal(0, body2.Closed);
    }

    [Fact]
    public async Task RefreshConstituents_ConflictFreeSnapshot_UsesSingleSaveChangesAndClosesMissing()
    {
        await using var context = await CountingAppDbContext.CreateSqliteAsync();
        var old = new Stock
        {
            Ticker = "OLD",
            Name = "Old Corp",
            CommonName = "Old Corp",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "OLD",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        context.Stocks.Add(old);
        await context.SaveChangesAsync();
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            MarketIndexId = 1,
            StockId = old.Id,
            EffectiveFrom = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, null),
        });

        context.ResetSaveChangesAsyncCalls();
        var controller = CreateMarketIndicesController(context, provider);
        var result = await controller.RefreshConstituents(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);

        Assert.Equal(1, context.SaveChangesAsyncCalls);
        Assert.Equal(1, body.Added);
        Assert.Equal(1, body.Closed);
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 1 && x.EffectiveTo == null));
        Assert.NotNull((await context.StockMarketIndices.SingleAsync(x => x.MarketIndexId == 1 && x.StockId == old.Id)).EffectiveTo);
    }

    [Fact]
    public async Task RefreshConstituents_ExistingTrackedStock_IsReusedWithoutDemotion()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "Dow Jones", NormalizedName = "DOW JONES", Code = "DJIA",
            NormalizedCode = "DJIA", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var tracked = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nasdaq,
            CurrentPrice = 100m,
            ProviderSymbol = "AAPL",
            TrackingStatus = StockTrackingStatus.Tracked
        };
        context.Stocks.Add(tracked);
        await context.SaveChangesAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, null),
        });
        var controller = CreateMarketIndicesController(context, provider);
        await controller.RefreshConstituents(1);

        Assert.Equal(1, await context.Stocks.CountAsync());
        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(StockTrackingStatus.Tracked, persisted.TrackingStatus);
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 1 && x.EffectiveTo == null));
    }

    [Fact]
    public async Task RefreshConstituents_DuplicateKeyDuringSave_Returns409WithoutPartialChanges()
    {
        await using var context = await CountingAppDbContext.CreateSqliteAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, "US0378331005"),
        });

        context.ThrowDuplicateOnSave = true;
        context.ResetSaveChangesAsyncCalls();
        var controller = CreateMarketIndicesController(context, provider);
        var result = await controller.RefreshConstituents(1);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("Конкурентное обновление состава индекса. Повторите попытку.", conflict.Value);
        Assert.Equal(1, context.SaveChangesAsyncCalls);
        Assert.Equal(0, await context.Stocks.CountAsync());
        Assert.Equal(0, await context.StockMarketIndices.CountAsync());
    }

    [Fact]
    public async Task RefreshConstituents_ClosureUsesOnlyImportedSnapshotSet()
    {
        await using var context = CreateContext();
        context.MarketIndices.AddRange(
            new MarketIndex
            {
                Id = 1, Name = "Dow Jones", NormalizedName = "DOW JONES", Code = "DJIA",
                NormalizedCode = "DJIA", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            },
            new MarketIndex
            {
                Id = 2, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX",
                NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        var oldStock = new Stock
        {
            Ticker = "OLD",
            Name = "Old Corp",
            CommonName = "Old Corp",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "OLD",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        var unrelatedStock = new Stock
        {
            Ticker = "OTHER",
            Name = "Other Corp",
            CommonName = "Other Corp",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "OTHER",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        context.Stocks.AddRange(oldStock, unrelatedStock);
        await context.SaveChangesAsync();
        context.StockMarketIndices.AddRange(
            new StockMarketIndex { MarketIndexId = 1, StockId = oldStock.Id, EffectiveFrom = DateTime.UtcNow, ImportedAt = DateTime.UtcNow },
            new StockMarketIndex { MarketIndexId = 2, StockId = unrelatedStock.Id, EffectiveFrom = DateTime.UtcNow, ImportedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, null),
        });
        var controller = CreateMarketIndicesController(context, provider);
        await controller.RefreshConstituents(1);

        Assert.Single(await context.StockMarketIndices.Where(x => x.MarketIndexId == 1 && x.EffectiveTo == null).ToListAsync());
        var closedMembership = await context.StockMarketIndices.SingleAsync(x => x.MarketIndexId == 1 && x.StockId == oldStock.Id);
        Assert.NotNull(closedMembership.EffectiveTo);
        var unrelatedMembership = await context.StockMarketIndices.SingleAsync(x => x.MarketIndexId == 2 && x.StockId == unrelatedStock.Id);
        Assert.Null(unrelatedMembership.EffectiveTo);
    }

    [Fact]
    public async Task RefreshConstituents_AmbiguousExchange_BecomesConflictAndDoesNotClose()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1, Name = "Dow Jones", NormalizedName = "DOW JONES", Code = "DJIA",
            NormalizedCode = "DJIA", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var existing = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nasdaq,
            ProviderSymbol = "AAPL",
            TrackingStatus = StockTrackingStatus.CatalogOnly
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            MarketIndexId = 1,
            StockId = existing.Id,
            EffectiveFrom = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SnapshotProvider(new[]
        {
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", "", null), // ambiguous -> conflict
        });
        var controller = CreateMarketIndicesController(context, provider);
        var result = await controller.RefreshConstituents(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);

        Assert.Equal("Partial", body.ProviderStatus);
        Assert.Equal(1, body.Conflicts);
        Assert.Equal(0, body.Closed);
        Assert.Null((await context.StockMarketIndices.SingleAsync(x => x.MarketIndexId == 1 && x.StockId == existing.Id)).EffectiveTo);
    }

    [Fact]
    public async Task RefreshAndGetConstituents_Nasdaq100CuratedSnapshot_ReturnsSourceAsOfAndCuratedMetadata()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 4, Name = "NASDAQ-100", NormalizedName = "NASDAQ-100", Code = "NDX",
            NormalizedCode = "NDX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var asOfDate = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
        var provider = new SnapshotProvider(
            entries:
            [
                new IndexConstituentEntry("GOOG", "GOOG", "Alphabet Inc. Class C", StockExchanges.Nasdaq, null),
                new IndexConstituentEntry("GOOGL", "GOOGL", "Alphabet Inc. Class A", StockExchanges.Nasdaq, null),
            ],
            providerName: "Nasdaq Global Indexes (curated snapshot)",
            sourceUrl: "https://www.nasdaq.com/market-activity/quotes/ndx-index",
            asOfDate: asOfDate);

        var controller = CreateMarketIndicesController(context, provider);
        var refreshResult = await controller.RefreshConstituents(4);
        var refreshOk = Assert.IsType<OkObjectResult>(refreshResult.Result);
        var refreshBody = Assert.IsType<IndexConstituentsRefreshResponse>(refreshOk.Value);

        Assert.Equal("Success", refreshBody.ProviderStatus);
        Assert.True(refreshBody.IsCuratedSnapshot);
        Assert.Equal(asOfDate, refreshBody.AsOfDate);
        Assert.Equal("https://www.nasdaq.com/market-activity/quotes/ndx-index", refreshBody.SourceUrl);

        var getResult = await controller.GetConstituents(4);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var getBody = Assert.IsType<IndexConstituentsResponse>(getOk.Value);

        Assert.Equal("Nasdaq Global Indexes (curated snapshot)", getBody.Source);
        Assert.True(getBody.IsCuratedSnapshot);
        Assert.Equal(asOfDate, getBody.AsOfDate);
    }

    [Fact]
    public async Task GetConstituents_MapsStockFieldsRequiredForChartFundamentalsAndPriceColumns()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5,
            Name = "Test Index",
            NormalizedName = "TEST INDEX",
            Code = "TST",
            NormalizedCode = "TST",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var stock = new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nasdaq,
            Wkn = "865985",
            Isin = "US0378331005",
            FinanzenNetSlug = "apple-aktie",
            CurrentPrice = 194.32m,
            CurrentPriceChange = 1.23m,
            CurrentPriceChangePercent = 0.64m,
            CurrentPriceAt = new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex
        {
            MarketIndexId = 5,
            StockId = stock.Id,
            Source = "snapshot",
            EffectiveFrom = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = CreateMarketIndicesController(context);
        var getResult = await controller.GetConstituents(5);
        var ok = Assert.IsType<OkObjectResult>(getResult.Result);
        var body = Assert.IsType<IndexConstituentsResponse>(ok.Value);
        var dto = Assert.Single(body.Constituents);

        Assert.Equal("AAPL", dto.Ticker);
        Assert.Equal("Apple Inc.", dto.Name);
        Assert.Equal("Apple", dto.CommonName);
        Assert.Equal(StockExchanges.Nasdaq, dto.Exchange);
        Assert.Equal("US0378331005", dto.Isin);
        Assert.Equal("865985", dto.Wkn);
        Assert.Equal("apple-aktie", dto.FinanzenNetSlug);
        Assert.Equal(194.32m, dto.CurrentPrice);
        Assert.Equal(1.23m, dto.CurrentPriceChange);
        Assert.Equal(0.64m, dto.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc), dto.CurrentPriceAt);
        Assert.Equal("CatalogOnly", dto.TrackingStatus);
    }

    // ── Relational regression: INSERT sentinel bug (MySQL DB default = 1) ─────
    //
    // This test uses SQLite (not InMemory) to validate the EF insert semantics that
    // caused the production incident: when TrackingStatus was configured with
    // HasDefaultValue(Tracked), EF omitted the column from INSERT when the value was
    // CatalogOnly = 0 (the CLR/int sentinel), letting the DB DEFAULT (1) win.
    // ValueGeneratedNever() must be present for this test to pass.

    [Fact]
    public async Task SqliteRelational_ConstituentInsert_PersistsCatalogOnlyNotTracked()
    {
        // Use a SQLite in-memory database so EF generates real SQL with column DEFAULT
        // semantics. InMemory provider does not evaluate HasDefaultValue and would always
        // pass this test even with the buggy configuration.
        await using var context = await CountingAppDbContext.CreateSqliteAsync();

        var constituent = new Stock
        {
            Ticker = "TEST",
            Name = "Test Corp",
            CommonName = "Test Corp",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "TEST",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(constituent);
        await context.SaveChangesAsync();

        var constituentId = constituent.Id;

        // Re-read with AsNoTracking to bypass the first-level identity cache,
        // ensuring we see exactly what was written to the SQLite database.
        var reread = await context.Stocks.AsNoTracking().SingleAsync(s => s.Id == constituentId);

        Assert.Equal(StockTrackingStatus.CatalogOnly, reread.TrackingStatus);
        Assert.Equal(0, (int)reread.TrackingStatus);
    }

    [Fact]
    public async Task SqliteRelational_StandardCreate_PersistsTracked()
    {
        await using var context = await CountingAppDbContext.CreateSqliteAsync();

        var stock = new Stock
        {
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var reread = await context.Stocks.AsNoTracking().SingleAsync(s => s.Id == stock.Id);

        Assert.Equal(StockTrackingStatus.Tracked, reread.TrackingStatus);
        Assert.Equal(1, (int)reread.TrackingStatus);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static StocksController CreateController(AppDbContext context)
    {
        SeedTrackedMemberships(context);
        return new StocksController(
            context,
            new NullStockHistoryService(),
            new StockPerformanceCalculationService(context, TimeProvider.System),
            new StockQuoteSnapshotPersistenceService(
                context,
                TimeProvider.System,
                NullLogger<StockQuoteSnapshotPersistenceService>.Instance),
            NullLogger<StocksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = TestUser(),
                }
            }
        };
    }

    private static void SeedTrackedMemberships(AppDbContext context)
    {
        if (!context.Users.Any(x => x.Id == 1))
        {
            context.Users.Add(new User { Id = 1, Username = "test", NormalizedUsername = "TEST", Email = "test@example.com", NormalizedEmail = "TEST@EXAMPLE.COM", CreatedAt = DateTime.UtcNow });
            context.SaveChanges();
        }

        foreach (var stock in context.Stocks.Where(x => x.TrackingStatus == StockTrackingStatus.Tracked).ToList())
        {
            if (context.UserTrackedStocks.Any(x => x.UserId == 1 && x.StockId == stock.Id)) continue;
            context.UserTrackedStocks.Add(new UserTrackedStock { UserId = 1, StockId = stock.Id, CreatedAtUtc = DateTime.UtcNow });
        }
        context.SaveChanges();
    }

    private static ClaimsPrincipal TestUser(int userId = 1) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"));

    private static MarketIndicesController CreateMarketIndicesController(
        AppDbContext context,
        IIndexConstituentsProvider? provider = null)
    {
        return new MarketIndicesController(
            context,
            new NullMarketIndexHistoryService(),
            provider ?? new NullIndexConstituentsProvider(),
            new NullStockHistoryService(),
            new StockPerformanceCalculationService(context, TimeProvider.System),
            new NullIndexConstituentHistoryRefreshJobService(),
            new NullIndexConstituentsBatchQuoteRefreshJobService(),
            NullLogger<MarketIndicesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestUser() }
            }
        };
    }

    private sealed class NullIndexConstituentHistoryRefreshJobService : IIndexConstituentHistoryRefreshJobService
    {
        public IndexConstituentHistoryRefreshJobEnqueueResult Enqueue(int marketIndexId, int stockId)
            => new()
            {
                Status = IndexConstituentHistoryRefreshJobEnqueueStatus.Enqueued,
                Job = new IndexConstituentHistoryRefreshJobResponse
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    MarketIndexId = marketIndexId,
                    StockId = stockId,
                    State = IndexConstituentHistoryRefreshJobState.Queued,
                    CreatedAtUtc = DateTime.UtcNow
                }
            };

        public bool TryGetJob(int marketIndexId, int stockId, string jobId, out IndexConstituentHistoryRefreshJobResponse? job)
        {
            job = null;
            return false;
        }
    }

    private sealed class NullIndexConstituentsBatchQuoteRefreshJobService : IIndexConstituentsBatchQuoteRefreshJobService
    {
        public IndexConstituentsBatchQuoteRefreshJobEnqueueResult Enqueue(int marketIndexId)
            => new() { Status = IndexConstituentsBatchQuoteRefreshJobEnqueueStatus.QueueFull };

        public bool TryGetJob(int marketIndexId, string jobId, out IndexConstituentsBatchQuoteRefreshJobResponse? job)
        {
            job = null;
            return false;
        }
    }

    private sealed class NullStockHistoryService : IStockHistoryService
    {
        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryResponse());

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryRefreshResponse { StockId = stock.Id });
        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullMarketIndexHistoryService : IMarketIndexHistoryService
    {
        public Task<MarketIndexHistoryResponse> GetHistoryAsync(MarketIndex index, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new MarketIndexHistoryResponse { MarketIndexId = index.Id, Range = range, Interval = "1d" });

        public Task<MarketIndexRefreshResponse> RefreshHistoryAsync(MarketIndex index, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new MarketIndexRefreshResponse { MarketIndexId = index.Id });
    }

    private sealed class NullIndexConstituentsProvider : IIndexConstituentsProvider
    {
        public string ProviderName => "Null";

        public Task<IndexConstituentsResult> GetConstituentsAsync(MarketIndex index, CancellationToken cancellationToken = default)
            => Task.FromResult(IndexConstituentsResult.Unsupported(ProviderName));
    }

    private sealed class SnapshotProvider : IIndexConstituentsProvider
    {
        private readonly IReadOnlyList<IndexConstituentEntry> _entries;
        private readonly string _providerName;
        private readonly DateTime _asOfDate;
        private readonly string? _sourceUrl;

        public SnapshotProvider(
            IReadOnlyList<IndexConstituentEntry> entries,
            string providerName = "Test Snapshot",
            string? sourceUrl = "https://example.test/djia",
            DateTime? asOfDate = null)
        {
            _entries = entries;
            _providerName = providerName;
            _sourceUrl = sourceUrl;
            _asOfDate = asOfDate ?? new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
        }

        public string ProviderName => _providerName;

        public Task<IndexConstituentsResult> GetConstituentsAsync(MarketIndex index, CancellationToken cancellationToken = default)
            => Task.FromResult(new IndexConstituentsResult(
                IndexConstituentsStatus.Success,
                ProviderName,
                DateTime.UtcNow,
                _entries,
                Message: null,
                AsOfDate: _asOfDate,
                SourceUrl: _sourceUrl,
                IsCuratedSnapshot: true,
                IsStale: false));
    }

    private sealed class CountingAppDbContext : AppDbContext
    {
        private readonly SqliteConnection _connection;

        private CountingAppDbContext(DbContextOptions<AppDbContext> options, SqliteConnection connection)
            : base(options)
        {
            _connection = connection;
        }

        public int SaveChangesAsyncCalls { get; private set; }
        public bool ThrowDuplicateOnSave { get; set; }

        public static async Task<CountingAppDbContext> CreateSqliteAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new CountingAppDbContext(options, connection);
            await context.Database.EnsureCreatedAsync();
            return context;
        }

        public void ResetSaveChangesAsyncCalls() => SaveChangesAsyncCalls = 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesAsyncCalls++;
            if (ThrowDuplicateOnSave)
            {
                throw new DbUpdateException(
                   "Duplicate key test exception",
                   new InvalidOperationException("Duplicate entry"));
            }

            return base.SaveChangesAsync(cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
