using FinanceApp.API.Controllers;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FinanceApp.Core.Tests;

public class StocksControllerTests
{
    [Fact]
    public async Task Create_BlankExchange_DefaultsToNyse()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = "   ",
            CurrentPrice = 123.45m
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);

        Assert.Equal(StockExchanges.Nyse, stock.Exchange);
        Assert.Equal(StockExchanges.Nyse, await context.Stocks.Select(x => x.Exchange).SingleAsync());
    }

    [Theory]
    [InlineData("nyse", StockExchanges.Nyse)]
    [InlineData(" nasdaq ", StockExchanges.Nasdaq)]
    [InlineData(" Frankfurt ", StockExchanges.Frankfurt)]
    public async Task Create_ValidExchangeValues_AreNormalized(string inputExchange, string expectedExchange)
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "TEST",
            Name = "Test Corp",
            CommonName = "Test Corp",
            Exchange = inputExchange,
            CurrentPrice = 1m
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);

        Assert.Equal(expectedExchange, stock.Exchange);
    }

    [Fact]
    public async Task Create_UnsupportedExchange_IsRejected()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "TEST",
            Name = "Test Corp",
            CommonName = "Test Corp",
            Exchange = "LSE",
            CurrentPrice = 1m
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);

        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.Contains(nameof(Stock.Exchange), problem.Errors.Keys);
        Assert.Empty(context.Stocks);
    }

    [Fact]
    public async Task Create_BlankCommonName_FallsBackToName()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = " Apple Inc. ",
            CommonName = "   ",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 123.45m
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);

        Assert.Equal("Apple Inc.", stock.Name);
        Assert.Equal("Apple Inc.", stock.CommonName);
    }

    [Fact]
    public async Task Create_SameIsinOnDifferentExchanges_IsAllowed()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var first = await controller.Create(new Stock
        {
            Ticker = "SAP",
            Name = "SAP SE Frankfurt",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            Isin = " de0007164600 "
        });

        var second = await controller.Create(new Stock
        {
            Ticker = "SAP",
            Name = "SAP ADR",
            CommonName = "SAP",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 101m,
            Isin = "DE0007164600"
        });

        Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.IsType<CreatedAtActionResult>(second.Result);
        Assert.Equal(2, await context.Stocks.CountAsync());
        Assert.All(await context.Stocks.OrderBy(x => x.Id).ToListAsync(), stock => Assert.Equal("DE0007164600", stock.Isin));
    }

    [Fact]
    public async Task Create_DuplicateTickerAndExchange_IsRejectedEvenWhenIsinDiffers()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var first = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 190m,
            Isin = "US0378331005"
        });

        var second = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Clone",
            CommonName = "Apple Clone",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 191m,
            Isin = "US0378331006"
        });

        Assert.IsType<CreatedAtActionResult>(first.Result);
        var badRequest = Assert.IsType<BadRequestObjectResult>(second.Result);
        Assert.Equal("Акция с тикером «AAPL» на бирже «NYSE» уже существует.", badRequest.Value);
        Assert.Single(context.Stocks);
    }

    [Fact]
    public async Task Create_SameWknOnDifferentListings_IsAllowed()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var first = await controller.Create(new Stock
        {
            Ticker = "SAP",
            Name = "SAP SE Frankfurt",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 120m,
            Wkn = "716460"
        });

        var second = await controller.Create(new Stock
        {
            Ticker = "SAP",
            Name = "SAP ADR",
            CommonName = "SAP",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 121m,
            Wkn = "716460"
        });

        Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.IsType<CreatedAtActionResult>(second.Result);
        Assert.Equal(2, await context.Stocks.CountAsync());
    }

    [Fact]
    public async Task Update_PriceOnlyRefresh_PreservesExistingMetadata()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 7,
            Ticker = "APC.F",
            Name = "Apple Inc. Frankfurt",
            CommonName = "Apple Inc.",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Wkn = "865985",
            Isin = "US0378331005"
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = existing.Name,
            CommonName = existing.CommonName,
            Wkn = existing.Wkn,
            Isin = existing.Isin,
            CurrentPrice = 101.23m,
        });

        Assert.IsType<NoContentResult>(result);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(101.23m, persisted.CurrentPrice);
        Assert.Equal("Apple Inc.", persisted.CommonName);
        Assert.Equal(StockExchanges.Frankfurt, persisted.Exchange);
        Assert.Equal("865985", persisted.Wkn);
        Assert.Equal("US0378331005", persisted.Isin);
        Assert.Equal("Apple Inc. Frankfurt", persisted.Name);
        Assert.Equal("APC.F", persisted.Ticker);
    }

    [Fact]
    public async Task Update_WithQuoteSnapshot_PersistsChangeAndTimestampAtomically()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 1,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple Inc.",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 190m,
            UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var providerTs = new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc);
        var controller = CreateController(context);

        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 195.40m,
            CurrentPriceChange = 3.15m,
            CurrentPriceChangePercent = 1.30m,
            CurrentPriceAt = providerTs,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.Equal(existing.Id, response.StockId);
        Assert.Equal(195.40m, response.CurrentPrice);
        Assert.Equal(3.15m, response.CurrentPriceChange);
        Assert.Equal(1.30m, response.CurrentPriceChangePercent);
        Assert.Equal(providerTs, response.CurrentPriceAt);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(195.40m, persisted.CurrentPrice);
        Assert.Equal(3.15m, persisted.CurrentPriceChange);
        Assert.Equal(1.30m, persisted.CurrentPriceChangePercent);
        Assert.Equal(providerTs, persisted.CurrentPriceAt);

        var intradayPoint = await context.StockHistoricalPrices.SingleAsync(x => x.StockId == existing.Id && x.Interval == "10m");
        Assert.Equal(new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc), intradayPoint.Timestamp);
        Assert.Equal(195.40m, intradayPoint.Close);
        Assert.True(intradayPoint.IsQuoteDerived);
    }

    [Fact]
    public async Task Update_ManualPriceEdit_ClearsStaleSnapshotFields()
    {
        await using var context = CreateContext();
        var providerTs = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var existing = new Stock
        {
            Id = 2,
            Ticker = "MSFT",
            Name = "Microsoft Corporation",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 420m,
            UpdatedAt = providerTs,
            CurrentPriceChange = 5m,
            CurrentPriceChangePercent = 1.2m,
            CurrentPriceAt = providerTs,
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "Котировка задержана",
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        // Simulate a manual price edit via metadata endpoint: clears stale snapshot fields
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = existing.Name,
            CommonName = existing.CommonName,
            CurrentPrice = 400m,
        });

        Assert.IsType<NoContentResult>(result);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(400m, persisted.CurrentPrice);
        // Stale snapshot fields must be cleared to avoid showing outdated change/timestamp
        Assert.Null(persisted.CurrentPriceChange);
        Assert.Null(persisted.CurrentPriceChangePercent);
        Assert.Null(persisted.CurrentPriceAt);
        Assert.False(persisted.CurrentPriceIsDelayed);
        Assert.Null(persisted.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task Create_NewStock_HasNullSnapshotFields()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "GOOG",
            Name = "Alphabet Inc.",
            CommonName = "Google",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 185m,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);

        Assert.Null(stock.CurrentPriceChange);
        Assert.Null(stock.CurrentPriceChangePercent);
        Assert.Null(stock.CurrentPriceAt);
    }

    [Fact]
    public async Task ExistingStockRows_RemainValidAfterModelUpdate()
    {
        // Verifies backward compatibility: rows without snapshot fields are still valid
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 99,
            Ticker = "IBM",
            Name = "IBM Corp",
            CommonName = "IBM",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 130m,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            // Snapshot fields are null (as they would be for existing rows before migration)
            CurrentPriceChange = null,
            CurrentPriceChangePercent = null,
            CurrentPriceAt = null,
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var loaded = await context.Stocks.FindAsync(99);
        Assert.NotNull(loaded);
        Assert.Equal(130m, loaded.CurrentPrice);
        Assert.Null(loaded.CurrentPriceChange);
        Assert.Null(loaded.CurrentPriceChangePercent);
        Assert.Null(loaded.CurrentPriceAt);
    }

    [Fact]
    public async Task Delete_ReferencedStock_ReturnsConflictAndDoesNotDelete()
    {
        await using var context = CreateContext();
        var user = new User
        {
            Id = 10,
            Username = "user",
            Email = "user@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };
        var stock = new Stock
        {
            Id = 20,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow
        };
        var portfolio = new Portfolio
        {
            Id = 30,
            Name = "Main",
            UserId = user.Id,
            User = user,
            CreatedAt = DateTime.UtcNow
        };
        var item = new PortfolioItem
        {
            Id = 40,
            PortfolioId = portfolio.Id,
            Portfolio = portfolio,
            StockId = stock.Id,
            Stock = stock,
            Quantity = 1m,
            BuyPrice = 100m,
            BoughtAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Stocks.Add(stock);
        context.Portfolios.Add(portfolio);
        context.PortfolioItems.Add(item);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Delete(stock.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("Невозможно удалить акцию: она используется как минимум в одном портфеле.", conflict.Value);
        Assert.True(await context.Stocks.AnyAsync(s => s.Id == stock.Id));
    }

    [Fact]
    public async Task Delete_UnreferencedStock_ReturnsNoContentAndDeletes()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 50,
            Ticker = "MSFT",
            Name = "Microsoft Corporation",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 200m,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Delete(stock.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await context.Stocks.AnyAsync(s => s.Id == stock.Id));
    }

    [Fact]
    public async Task Delete_MissingStock_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Delete(999);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateIdentity_UnchangedNormalizedIdentity_ReturnsNoOpAndKeepsHistory()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 981,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = DateTime.UtcNow.Date,
            Open = 1m,
            High = 1m,
            Low = 1m,
            Close = 1m,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateIdentity(stock.Id, new UpdateStockIdentityRequest
        {
            Ticker = " aapl ",
            Exchange = " nyse ",
            ConfirmationText = "ignored",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockIdentityChangeResponse>(ok.Value);
        Assert.False(payload.IdentityChanged);
        Assert.Equal(1, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task UpdateIdentity_WithPortfolioDependency_ReturnsStructuredConflict()
    {
        await using var context = CreateContext();
        var user = new User { Id = 201, Username = "u", Email = "u@example.com", PasswordHash = "hash", CreatedAt = DateTime.UtcNow };
        var portfolio = new Portfolio { Id = 202, Name = "Main", UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow };
        var stock = new Stock
        {
            Id = 203,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(user);
        context.Portfolios.Add(portfolio);
        context.Stocks.Add(stock);
        context.PortfolioItems.Add(new PortfolioItem
        {
            PortfolioId = portfolio.Id,
            Portfolio = portfolio,
            StockId = stock.Id,
            Stock = stock,
            Quantity = 1,
            BuyPrice = 100m,
            BoughtAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var transition = "AAPL (NYSE) → AAPLX (NASDAQ)";
        var result = await controller.UpdateIdentity(stock.Id, new UpdateStockIdentityRequest
        {
            Ticker = "AAPLX",
            Exchange = StockExchanges.Nasdaq,
            ConfirmationText = transition,
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var payload = Assert.IsType<StockMutationBlockedResponse>(conflict.Value);
        Assert.Contains(payload.Diagnostics.Blockers, b => b.Category == "portfolioItems");
    }

    [Fact]
    public async Task UpdateIdentity_Success_ClearsDerivedDataAndResetsProviderSymbol()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 310,
            Ticker = "WPM",
            Name = "WPM",
            CommonName = "WPM",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 50m,
            ProviderSymbol = "WPM",
            CurrentPriceAt = DateTime.UtcNow,
            CurrentPriceChange = 1m,
            CurrentPriceChangePercent = 2m,
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "delay",
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = DateTime.UtcNow.Date,
            Open = 10,
            High = 11,
            Low = 9,
            Close = 10,
        });
        context.FundamentalsSnapshots.Add(new CompanyFundamentalsSnapshot
        {
            StockId = stock.Id,
            SourceSymbol = "WPM",
            FetchedAtUtc = DateTime.UtcNow,
        });
        context.StockMetadataEnrichmentResults.Add(new StockMetadataEnrichmentResult
        {
            JobId = Guid.NewGuid(),
            StockId = stock.Id,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var transition = "WPM (NYSE) → WPM (NASDAQ)";
        var result = await controller.UpdateIdentity(stock.Id, new UpdateStockIdentityRequest
        {
            Ticker = "WPM",
            Exchange = StockExchanges.Nasdaq,
            ConfirmationText = transition,
            RetainProviderSymbol = false,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockIdentityChangeResponse>(ok.Value);
        Assert.True(payload.IdentityChanged);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(StockExchanges.Nasdaq, persisted.Exchange);
        Assert.Null(persisted.ProviderSymbol);
        Assert.Equal(0m, persisted.CurrentPrice);
        Assert.Equal(0, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(0, await context.FundamentalsSnapshots.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(0, await context.StockMetadataEnrichmentResults.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task UpdateEdit_IdentityAndMetadata_AppliesAtomicallyAndClearsUnchangedLegacyIdentifiers()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 311,
            Ticker = "ALMTF",
            Name = "Almonty Industries",
            CommonName = "Almonty",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            Wkn = "A1JSSD",
            Isin = "CA0203981034",
            FinanzenNetSlug = "almonty-industries-aktie",
            ProviderSymbol = "ALMTF",
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = DateTime.UtcNow.Date,
            Open = 10,
            High = 11,
            Low = 9,
            Close = 10,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateEdit(stock.Id, new UpdateStockEditRequest
        {
            Ticker = "ALM",
            Exchange = StockExchanges.Nasdaq,
            Name = "Almonty Industries Corp.",
            CommonName = "Almonty",
            Wkn = "A1JSSD",
            Isin = "CA0203981034",
            FinanzenNetSlug = "almonty-industries-aktie",
            CurrentPrice = 11m,
            ConfirmationText = "ALMTF (NYSE) → ALM (NASDAQ)",
            IdentityEditingEnabled = true,
            RetainProviderSymbol = false,
        });

        Assert.IsType<NoContentResult>(result);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == stock.Id);
        Assert.Equal("ALM", persisted.Ticker);
        Assert.Equal(StockExchanges.Nasdaq, persisted.Exchange);
        Assert.Equal("Almonty Industries Corp.", persisted.Name);
        Assert.Null(persisted.Wkn);
        Assert.Null(persisted.Isin);
        Assert.Null(persisted.FinanzenNetSlug);
        Assert.Null(persisted.ProviderSymbol);
        Assert.Equal(0, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task UpdateEdit_InvalidMetadata_DoesNotApplyIdentityChange()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 312,
            Ticker = "ALMTF",
            Name = "Almonty Industries",
            CommonName = "Almonty",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateEdit(stock.Id, new UpdateStockEditRequest
        {
            Ticker = "ALM",
            Exchange = StockExchanges.Nasdaq,
            Name = "Almonty Industries",
            CommonName = "Almonty",
            Wkn = "BAD",
            CurrentPrice = 11m,
            ConfirmationText = "ALMTF (NYSE) → ALM (NASDAQ)",
            IdentityEditingEnabled = true,
            RetainProviderSymbol = false,
        });

        Assert.IsType<BadRequestObjectResult>(result);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == stock.Id);
        Assert.Equal("ALMTF", persisted.Ticker);
        Assert.Equal(StockExchanges.Nyse, persisted.Exchange);
        Assert.Equal("Almonty Industries", persisted.Name);
    }

    [Fact]
    public async Task DeletePermanent_WithDependencies_ReturnsStructuredConflict()
    {
        await using var context = CreateContext();
        var user = new User { Id = 401, Username = "u1", Email = "u1@example.com", PasswordHash = "hash", CreatedAt = DateTime.UtcNow };
        var portfolio = new Portfolio { Id = 402, Name = "Main", UserId = user.Id, User = user, CreatedAt = DateTime.UtcNow };
        var stock = new Stock
        {
            Id = 403,
            Ticker = "ABCD",
            Name = "ABCD",
            CommonName = "ABCD",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 1m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(user);
        context.Portfolios.Add(portfolio);
        context.Stocks.Add(stock);
        context.Orders.Add(new Order
        {
            PortfolioId = portfolio.Id,
            Portfolio = portfolio,
            StockId = stock.Id,
            Stock = stock,
            Type = OrderType.Buy,
            Quantity = 1,
            Price = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.DeletePermanent(stock.Id, new DeleteStockPermanentRequest { ConfirmationText = stock.Ticker });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = Assert.IsType<StockMutationBlockedResponse>(conflict.Value);
        Assert.Contains(payload.Diagnostics.Blockers, b => b.Category == "orders");
        Assert.True(await context.Stocks.AnyAsync(x => x.Id == stock.Id));
    }

    [Fact]
    public async Task DeletePermanent_UnreferencedStock_DeletesStockAndDerivedRows()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 501,
            Ticker = "FREE",
            Name = "Free",
            CommonName = "Free",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Interval = "1d",
            Timestamp = DateTime.UtcNow.Date,
            Open = 1,
            High = 1,
            Low = 1,
            Close = 1,
        });
        context.FundamentalsSnapshots.Add(new CompanyFundamentalsSnapshot
        {
            StockId = stock.Id,
            SourceSymbol = "FREE",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.DeletePermanent(stock.Id, new DeleteStockPermanentRequest { ConfirmationText = "УДАЛИТЬ" });

        Assert.IsType<NoContentResult>(result);
        Assert.False(await context.Stocks.AnyAsync(x => x.Id == stock.Id));
        Assert.Equal(0, await context.StockHistoricalPrices.CountAsync(x => x.StockId == stock.Id));
        Assert.Equal(0, await context.FundamentalsSnapshots.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task DeletePermanent_WithoutBodyConfirmation_StillDeletes()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 502,
            Ticker = "NOCONF",
            Name = "No Confirm",
            CommonName = "No Confirm",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.DeletePermanent(stock.Id, null);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await context.Stocks.AnyAsync(x => x.Id == stock.Id));
    }

    [Fact]
    public async Task DeletePermanent_UnexpectedForeignKeyConflict_ReturnsStructuredConflict()
    {
        await using var context = await CreateSqliteRelationalContextAsync();
        var stock = new Stock
        {
            Id = 503,
            Ticker = "FKFAIL",
            Name = "Foreign Key Failure",
            CommonName = "Foreign Key Failure",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE UnknownStockRefs (Id INTEGER PRIMARY KEY AUTOINCREMENT, StockId INTEGER NOT NULL, FOREIGN KEY(StockId) REFERENCES Stocks(Id) ON DELETE RESTRICT);");
        await context.Database.ExecuteSqlRawAsync("INSERT INTO UnknownStockRefs (StockId) VALUES ({0});", stock.Id);

        var controller = CreateController(context);
        var result = await controller.DeletePermanent(stock.Id, null);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = Assert.IsType<StockMutationBlockedResponse>(conflict.Value);
        Assert.Contains(payload.Diagnostics.Blockers, b => b.Category == "unexpectedForeignKeyConflict");
        Assert.True(await context.Stocks.AnyAsync(x => x.Id == stock.Id));
    }


    [Fact]
    public async Task GetHistory_MissingStock_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.GetHistory(999, "1y");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetHistory_InvalidRange_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 76,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 1m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService();
        var controller = CreateController(context, service);
        var result = await controller.GetHistory(76, "bad-range");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid range", Assert.IsType<string>(badRequest.Value), StringComparison.Ordinal);
        Assert.Empty(service.GetHistoryCalls);
    }

    [Fact]
    public async Task GetHistory_CatalogOnlyStock_AllowsGeneralEndpointWithoutMembershipMutation()
    {
        await using var context = CreateContext();
        var stock = new Stock
        {
            Id = 78,
            Ticker = "MCD",
            Name = "McDonald's Corporation",
            CommonName = "McDonald's",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 300m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var expected = new StockHistoryResponse
        {
            Range = "1y",
            Interval = "1d",
            Points =
            [
                new StockHistoryPointResponse
                {
                    Timestamp = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    Interval = "1d",
                    OpenRaw = 300m,
                    HighRaw = 305m,
                    LowRaw = 299m,
                    CloseRaw = 304m,
                    OpenNormalized = 300m,
                    HighNormalized = 305m,
                    LowNormalized = 299m,
                    CloseNormalized = 304m,
                    OpenEur = 300m,
                    HighEur = 305m,
                    LowEur = 299m,
                    CloseEur = 304m,
                    Volume = 1000
                }
            ]
        };
        var service = new RecordingStockHistoryService
        {
            HistoryResponseFactory = (_, range) => new StockHistoryResponse
            {
                Range = range,
                Interval = expected.Interval,
                Points = expected.Points
            }
        };
        var controller = CreateController(context, service);

        var result = await controller.GetHistory(stock.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<StockHistoryResponse>(ok.Value);
        Assert.Equal("1y", payload.Range);
        Assert.Single(payload.Points);
        Assert.Equal((stock.Id, "1y"), Assert.Single(service.GetHistoryCalls));
        Assert.Equal(1, await context.Stocks.CountAsync());
        Assert.Empty(await context.StockMarketIndices.ToListAsync());
        var persisted = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(stock.Id, persisted.Id);
        Assert.Equal(StockTrackingStatus.CatalogOnly, persisted.TrackingStatus);
    }

    [Fact]
    public async Task RefreshHistory_MissingStock_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.RefreshHistory(999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RefreshHistory_BlankTicker_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 77,
            Ticker = "   ",
            Name = "No Ticker",
            CommonName = "No Ticker",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 1m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService();
        var controller = CreateController(context, service);
        var result = await controller.RefreshHistory(77);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("тикер", Assert.IsType<string>(badRequest.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.RefreshHistoryCalls);
    }

    [Fact]
    public async Task RefreshHistory_InvalidExchange_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 79,
            Ticker = "MCD",
            Name = "McDonald's Corporation",
            CommonName = "McDonald's",
            Exchange = "INVALID",
            CurrentPrice = 1m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService();
        var controller = CreateController(context, service);
        var result = await controller.RefreshHistory(79);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("биржа", Assert.IsType<string>(badRequest.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.RefreshHistoryCalls);
    }

    [Fact]
    public async Task RefreshHistory_CatalogOnlyStock_AllowsManualRefreshWithoutIdentityMutation()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var stock = new Stock
        {
            Id = 80,
            Ticker = "MCD",
            Name = "McDonald's Corporation",
            CommonName = "McDonald's",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 300m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        };
        context.Stocks.Add(stock);
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 11,
            Name = "Dow Jones",
            NormalizedName = "DOW JONES",
            Code = "DJI",
            NormalizedCode = "DJI",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = stock.Id,
            MarketIndexId = 11,
            EffectiveFrom = now,
            ImportedAt = now,
            Source = "Test"
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService
        {
            RefreshResponseFactory = stockArg => new StockHistoryRefreshResponse
            {
                StockId = stockArg.Id,
                DeletedPoints = 2,
                ImportedPoints = 5,
                RateLimited = true
            }
        };
        var controller = CreateController(context, service);

        var result = await controller.RefreshHistory(stock.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockHistoryRefreshResponse>(ok.Value);
        Assert.Equal(stock.Id, payload.StockId);
        Assert.True(payload.RateLimited);
        Assert.Equal((stock.Id, StockExchanges.Nyse), Assert.Single(service.RefreshHistoryCalls));
        Assert.Equal(1, await context.Stocks.CountAsync());
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == stock.Id));
        var persisted = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(stock.Id, persisted.Id);
        Assert.Equal(StockTrackingStatus.CatalogOnly, persisted.TrackingStatus);
    }

    [Fact]
    public async Task GetHistoryRoutingDiagnostics_ReturnsEffectiveSymbolFromService()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 81,
            Ticker = "SMEGF",
            Name = "Siemens Energy AG",
            CommonName = "Siemens Energy AG",
            Exchange = StockExchanges.Frankfurt,
            ProviderSymbol = "ENR.DE",
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService
        {
            RoutingDiagnosticsFactory = (stock, _) => new StockHistoryRepairDiagnosticsResponse
            {
                StockId = stock.Id,
                Ticker = stock.Ticker,
                Exchange = stock.Exchange,
                Name = stock.Name,
                ConfiguredProviderSymbol = stock.ProviderSymbol,
                EffectiveProviderSymbol = stock.ProviderSymbol!,
                ResultBucket = "success",
            }
        };
        var controller = CreateController(context, service);

        var result = await controller.GetHistoryRoutingDiagnostics(81);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockHistoryRepairDiagnosticsResponse>(ok.Value);
        Assert.Equal("ENR.DE", payload.EffectiveProviderSymbol);
    }

    [Fact]
    public async Task ValidateHistoryProviderSymbol_ForwardsCandidateToService()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 82,
            Ticker = "SMEGF",
            Name = "Siemens Energy AG",
            CommonName = "Siemens Energy AG",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService();
        var controller = CreateController(context, service);

        var result = await controller.ValidateHistoryProviderSymbol(82, new StockHistoryProviderSymbolValidationRequest
        {
            CandidateProviderSymbol = "ENR.DE"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal((82, "ENR.DE"), Assert.Single(service.ValidateCalls));
    }

    [Fact]
    public async Task HardResetHistory_RequiresTypedConfirmation()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 83,
            Ticker = "SMEGF",
            Name = "Siemens Energy AG",
            CommonName = "Siemens Energy AG",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 10m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new RecordingStockHistoryService();
        var controller = CreateController(context, service);

        var badResult = await controller.HardResetHistory(83, new StockHistoryHardResetRequest
        {
            CandidateProviderSymbol = "ENR.DE",
            ConfirmationText = "NOPE",
        });
        Assert.IsType<BadRequestObjectResult>(badResult.Result);
        Assert.Empty(service.HardResetCalls);

        var okResult = await controller.HardResetHistory(83, new StockHistoryHardResetRequest
        {
            CandidateProviderSymbol = "ENR.DE",
            ConfirmationText = "УДАЛИТЬ",
        });
        Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.Equal((83, "ENR.DE"), Assert.Single(service.HardResetCalls));
    }

    [Fact]
    public async Task Create_WithUnderscoreSlug_Accepted()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "WDC",
            Name = "Western Digital",
            CommonName = "Western Digital",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 50m,
            FinanzenNetSlug = "western_digital-aktie"
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);
        Assert.Equal("western_digital-aktie", stock.FinanzenNetSlug);
    }

    [Fact]
    public async Task Create_WithSlashInSlug_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "WDC",
            Name = "Western Digital",
            CommonName = "Western Digital",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 50m,
            FinanzenNetSlug = "invalid/slug"
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuote_UpdatesOnlyQuoteFields_DoesNotTouchIdentity()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 101,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 180m,
            Wkn = "865985",
            Isin = "US0378331005",
            FinanzenNetSlug = "apple-aktie",
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var providerTs = new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc);
        var controller = CreateController(context);

        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 195.50m,
            CurrentPriceChange = 3.25m,
            CurrentPriceChangePercent = 1.69m,
            CurrentPriceAt = providerTs,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.Equal(existing.Id, response.StockId);
        Assert.Equal(195.50m, response.CurrentPrice);
        Assert.Equal(3.25m, response.CurrentPriceChange);
        Assert.Equal(1.69m, response.CurrentPriceChangePercent);
        Assert.Equal(providerTs, response.CurrentPriceAt);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(195.50m, persisted.CurrentPrice);
        Assert.Equal(3.25m, persisted.CurrentPriceChange);
        Assert.Equal(1.69m, persisted.CurrentPriceChangePercent);
        Assert.Equal(providerTs, persisted.CurrentPriceAt);

        // Identity fields must NOT be modified
        Assert.Equal("AAPL", persisted.Ticker);
        Assert.Equal("Apple Inc.", persisted.Name);
        Assert.Equal("Apple", persisted.CommonName);
        Assert.Equal(StockExchanges.Nyse, persisted.Exchange);
        Assert.Equal("865985", persisted.Wkn);
        Assert.Equal("US0378331005", persisted.Isin);
        Assert.Equal("apple-aktie", persisted.FinanzenNetSlug);
    }

    [Fact]
    public async Task UpdateQuote_MissingStock_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.UpdateQuote(999, new UpdateStockQuoteRequest
        {
            CurrentPrice = 100m,
        });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuote_NullableFields_CanBeNull()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 102,
            Ticker = "MSFT",
            Name = "Microsoft Corporation",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 400m,
            UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 410m,
            CurrentPriceChange = null,
            CurrentPriceChangePercent = null,
            CurrentPriceAt = null,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.Equal(410m, response.CurrentPrice);
        Assert.Null(response.CurrentPriceChange);
        Assert.Null(response.CurrentPriceChangePercent);
        Assert.Null(response.CurrentPriceAt);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(410m, persisted.CurrentPrice);
        Assert.Null(persisted.CurrentPriceChange);
        Assert.Null(persisted.CurrentPriceChangePercent);
        Assert.Null(persisted.CurrentPriceAt);
    }

    [Fact]
    public async Task UpdateQuote_NewerDelayedSnapshot_PersistsDelayedMetadata()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 106,
            Ticker = "MTE.F",
            Name = "Seagate",
            CommonName = "Seagate",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 804m,
            CurrentPriceChange = -44m,
            CurrentPriceChangePercent = -5.19m,
            CurrentPriceAt = new DateTime(2026, 8, 18, 12, 17, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 18, 12, 17, 5, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 752m,
            CurrentPriceChange = -52m,
            CurrentPriceChangePercent = -6.47m,
            CurrentPriceAt = new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc),
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "Котировка задержана на 15 минут   ",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.True(response.CurrentPriceIsDelayed);
        Assert.Equal("Котировка задержана на 15 минут", response.CurrentPriceDelayWarning);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(752m, persisted.CurrentPrice);
        Assert.Equal(-52m, persisted.CurrentPriceChange);
        Assert.Equal(-6.47m, persisted.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc), persisted.CurrentPriceAt);
        Assert.True(persisted.CurrentPriceIsDelayed);
        Assert.Equal("Котировка задержана на 15 минут", persisted.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task UpdateQuote_EqualTimestamp_PrefersNonDelayedSnapshot()
    {
        await using var context = CreateContext();
        var timestamp = new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc);
        context.Stocks.Add(new Stock
        {
            Id = 107,
            Ticker = "MTE.F",
            Name = "Seagate",
            CommonName = "Seagate",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 752m,
            CurrentPriceChange = -52m,
            CurrentPriceChangePercent = -6.47m,
            CurrentPriceAt = timestamp,
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "Котировка задержана",
            UpdatedAt = timestamp,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(107, new UpdateStockQuoteRequest
        {
            CurrentPrice = 752m,
            CurrentPriceChange = -52m,
            CurrentPriceChangePercent = -6.47m,
            CurrentPriceAt = timestamp,
            CurrentPriceIsDelayed = false,
            CurrentPriceDelayWarning = null,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.False(response.CurrentPriceIsDelayed);
        Assert.Null(response.CurrentPriceDelayWarning);

        var persisted = await context.Stocks.SingleAsync();
        Assert.False(persisted.CurrentPriceIsDelayed);
        Assert.Null(persisted.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task UpdateQuote_InvalidTimestamp_DoesNotOverwriteValidStoredSnapshot()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 108,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 250m,
            CurrentPriceChange = 2m,
            CurrentPriceChangePercent = 0.8m,
            CurrentPriceAt = new DateTime(2026, 8, 19, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 19, 8, 0, 5, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(108, new UpdateStockQuoteRequest
        {
            CurrentPrice = 200m,
            CurrentPriceChange = -10m,
            CurrentPriceChangePercent = -4m,
            CurrentPriceAt = null,
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "Котировка задержана",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.False(response.Applied);
        Assert.Equal(250m, response.CurrentPrice);
        Assert.False(response.CurrentPriceIsDelayed);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(250m, persisted.CurrentPrice);
        Assert.Equal(new DateTime(2026, 8, 19, 8, 0, 0, DateTimeKind.Utc), persisted.CurrentPriceAt);
        Assert.False(persisted.CurrentPriceIsDelayed);
        Assert.Null(persisted.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task UpdateQuote_RaceCondition_DoesNotRevertMetadataEditedAfterQuoteFetch()
    {
        // Simulates: 1) quote fetch starts, 2) user edits metadata, 3) old quote response arrives
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 103,
            Ticker = "OLD.F",
            Name = "Old Name",
            CommonName = "Old",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        // Step 2: user edits metadata (name, etc.) — identity is immutable
        var editResult = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = "New Name",
            CommonName = "New",
            CurrentPrice = 100m,
        });
        Assert.IsType<NoContentResult>(editResult);

        // Step 3: stale quote request completes — uses quote-only endpoint
        var quoteResult = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 102m,
            CurrentPriceChange = 2m,
            CurrentPriceChangePercent = 2.0m,
            CurrentPriceAt = new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc),
        });
        var quoteOk = Assert.IsType<OkObjectResult>(quoteResult.Result);
        var quoteResponse = Assert.IsType<UpdateStockQuoteResponse>(quoteOk.Value);
        Assert.True(quoteResponse.Applied);

        var persisted = await context.Stocks.SingleAsync();
        // Price was updated by the quote
        Assert.Equal(102m, persisted.CurrentPrice);
        // Identity fields are unchanged (immutable)
        Assert.Equal("OLD.F", persisted.Ticker);
        Assert.Equal(StockExchanges.Frankfurt, persisted.Exchange);
        // Name reflects the metadata edit, NOT reverted by the quote
        Assert.Equal("New Name", persisted.Name);
    }

    [Fact]
    public async Task UpdateQuote_StaleProviderTimestamp_DoesNotOverwriteNewerStoredQuote()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 104,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 210m,
            CurrentPriceChange = 3m,
            CurrentPriceChangePercent = 1.45m,
            CurrentPriceAt = new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 1, 15, 0, 5, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 205m,
            CurrentPriceChange = -2m,
            CurrentPriceChangePercent = -0.97m,
            CurrentPriceAt = new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc),
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.False(response.Applied);
        Assert.Equal(210m, response.CurrentPrice);
        Assert.Equal(3m, response.CurrentPriceChange);
        Assert.Equal(1.45m, response.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc), response.CurrentPriceAt);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(210m, persisted.CurrentPrice);
        Assert.Equal(3m, persisted.CurrentPriceChange);
        Assert.Equal(1.45m, persisted.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 1, 15, 0, 0, DateTimeKind.Utc), persisted.CurrentPriceAt);
    }

    [Fact]
    public async Task UpdateQuote_EqualSnapshotTimestamp_HistoryOnlyRepair_IsReportedAsApplied()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 109,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 404.40m,
            CurrentPriceChange = 1.25m,
            CurrentPriceChangePercent = 0.31m,
            CurrentPriceAt = new DateTime(2026, 8, 20, 12, 47, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 20, 12, 47, 2, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = existing.Id,
            Interval = "10m",
            Timestamp = new DateTime(2026, 8, 20, 11, 40, 0, DateTimeKind.Utc),
            Open = 400m,
            High = 401m,
            Low = 399m,
            Close = 400.5m,
            QuoteCurrency = "EUR",
            FinancialCurrency = "EUR",
            NormalizedQuoteCurrency = "EUR",
            QuoteUnitMultiplier = 1m,
            Volume = 1000,
            IsQuoteDerived = false
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 404.40m,
            CurrentPriceChange = 1.25m,
            CurrentPriceChangePercent = 0.31m,
            CurrentPriceAt = new DateTime(2026, 8, 20, 12, 47, 0, DateTimeKind.Utc),
            CurrentPriceIsDelayed = false,
            CurrentPriceDelayWarning = null,
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.False(response.SnapshotApplied);
        Assert.True(response.HistoryApplied);

        var quoteDerivedPoint = await context.StockHistoricalPrices
            .SingleOrDefaultAsync(x => x.StockId == existing.Id && x.Interval == "10m" && x.Timestamp == new DateTime(2026, 8, 20, 12, 40, 0, DateTimeKind.Utc) && x.IsQuoteDerived);
        Assert.NotNull(quoteDerivedPoint);
    }

    [Fact]
    public async Task UpdateQuote_CatalogOnlyStock_PersistsSnapshotWithoutChangingTrackingStatus()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 105,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 250m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateQuote(existing.Id, new UpdateStockQuoteRequest
        {
            CurrentPrice = 251.23m,
            CurrentPriceChange = 1.23m,
            CurrentPriceChangePercent = 0.49m,
            CurrentPriceAt = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc),
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UpdateStockQuoteResponse>(ok.Value);
        Assert.True(response.Applied);
        Assert.Equal(existing.Id, response.StockId);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(251.23m, persisted.CurrentPrice);
        Assert.Equal(StockTrackingStatus.CatalogOnly, persisted.TrackingStatus);
    }

    // ─── New metadata endpoint tests ───────────────────────────────────────────

    [Fact]
    public async Task UpdateMetadata_ChangesAllAllowedFields_PreservesIdentity()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 200,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            Wkn = "716460",
            Isin = "DE0007164600",
            FinanzenNetSlug = "sap-aktie",
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = "SAP SE Updated",
            CommonName = "SAP Updated",
            Wkn = "716461",
            Isin = "DE0007164601",
            FinanzenNetSlug = "sap-aktie-new",
            CurrentPrice = 120m,
        });

        Assert.IsType<NoContentResult>(result);

        var persisted = await context.Stocks.SingleAsync();
        // Editable fields updated
        Assert.Equal("SAP SE Updated", persisted.Name);
        Assert.Equal("SAP Updated", persisted.CommonName);
        Assert.Equal("716461", persisted.Wkn);
        Assert.Equal("DE0007164601", persisted.Isin);
        Assert.Equal("sap-aktie-new", persisted.FinanzenNetSlug);
        Assert.Equal(120m, persisted.CurrentPrice);
        // Identity fields must NOT change
        Assert.Equal("SAP", persisted.Ticker);
        Assert.Equal(StockExchanges.Frankfurt, persisted.Exchange);
    }

    [Fact]
    public async Task UpdateMetadata_MissingStock_ReturnsNotFound()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.UpdateMetadata(999, new UpdateStockMetadataRequest
        {
            Name = "Ghost",
            CurrentPrice = 1m,
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateMetadata_ManualPrice_ClearsStaleSnapshotFields()
    {
        await using var context = CreateContext();
        var providerTs = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var existing = new Stock
        {
            Id = 201,
            Ticker = "MSFT",
            Name = "Microsoft Corporation",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 420m,
            CurrentPriceChange = 5m,
            CurrentPriceChangePercent = 1.2m,
            CurrentPriceAt = providerTs,
            UpdatedAt = providerTs,
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = existing.Name,
            CurrentPrice = 400m,
        });

        Assert.IsType<NoContentResult>(result);

        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal(400m, persisted.CurrentPrice);
        Assert.Null(persisted.CurrentPriceChange);
        Assert.Null(persisted.CurrentPriceChangePercent);
        Assert.Null(persisted.CurrentPriceAt);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidSlug_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 202,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = "Apple Inc.",
            FinanzenNetSlug = "invalid/slug",
            CurrentPrice = 100m,
        });

        Assert.IsType<BadRequestObjectResult>(result);
        // Stock must not be modified
        var persisted = await context.Stocks.SingleAsync();
        Assert.Null(persisted.FinanzenNetSlug);
    }

    [Fact]
    public async Task UpdateMetadata_SectorWithoutIndustry_PersistsDirectSector_AndProjectionReturnsIt()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var sector = new Sector
        {
            Id = 401,
            Name = "Information Technology",
            NormalizedName = "information technology",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.Add(sector);
        context.Stocks.Add(new Stock
        {
            Id = 402,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var updateResult = await controller.UpdateMetadata(402, new UpdateStockMetadataRequest
        {
            Name = "SAP SE",
            CommonName = "SAP",
            CurrentPrice = 101m,
            SectorId = sector.Id,
            IndustryId = null
        });

        Assert.IsType<NoContentResult>(updateResult);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == 402);
        Assert.Equal(sector.Id, persisted.SectorId);
        Assert.Null(persisted.IndustryId);

        var byId = await controller.GetById(402);
        var responseStock = Assert.IsType<Stock>(byId.Value);
        Assert.Equal(sector.Id, responseStock.Sector?.Id);
        Assert.Equal("Information Technology", responseStock.Sector?.Name);
        Assert.Null(responseStock.Industry);
    }

    [Fact]
    public async Task UpdateMetadata_ClearSectorAndIndustry_PersistsNulls()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var sector = new Sector
        {
            Id = 411,
            Name = "Health Care",
            NormalizedName = "health care",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var industry = new Industry
        {
            Id = 412,
            SectorId = sector.Id,
            Sector = sector,
            Name = "Biotechnology",
            NormalizedName = "biotechnology",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.Add(sector);
        context.Industries.Add(industry);
        context.Stocks.Add(new Stock
        {
            Id = 413,
            Ticker = "BAYN",
            Name = "Bayer AG",
            CommonName = "Bayer",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 50m,
            SectorId = sector.Id,
            IndustryId = industry.Id,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(413, new UpdateStockMetadataRequest
        {
            Name = "Bayer AG",
            CommonName = "Bayer",
            CurrentPrice = 51m,
            SectorId = null,
            IndustryId = null
        });

        Assert.IsType<NoContentResult>(result);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == 413);
        Assert.Null(persisted.SectorId);
        Assert.Null(persisted.IndustryId);
    }

    [Fact]
    public async Task UpdateMetadata_ConflictingSectorAndIndustry_UsesIndustrySector()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var sectorA = new Sector
        {
            Id = 421,
            Name = "Financials",
            NormalizedName = "financials",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var sectorB = new Sector
        {
            Id = 422,
            Name = "Information Technology",
            NormalizedName = "information technology",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var industryB = new Industry
        {
            Id = 423,
            SectorId = sectorB.Id,
            Sector = sectorB,
            Name = "Software",
            NormalizedName = "software",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.AddRange(sectorA, sectorB);
        context.Industries.Add(industryB);
        context.Stocks.Add(new Stock
        {
            Id = 424,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(424, new UpdateStockMetadataRequest
        {
            Name = "Microsoft",
            CommonName = "Microsoft",
            CurrentPrice = 101m,
            SectorId = sectorA.Id,
            IndustryId = industryB.Id
        });

        Assert.IsType<NoContentResult>(result);
        var persisted = await context.Stocks.SingleAsync(x => x.Id == 424);
        Assert.Equal(industryB.Id, persisted.IndustryId);
        Assert.Equal(sectorB.Id, persisted.SectorId);
    }

    [Fact]
    public async Task UpdateMetadata_UnknownSector_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 431,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(431, new UpdateStockMetadataRequest
        {
            Name = "Apple Inc.",
            CommonName = "Apple",
            CurrentPrice = 101m,
            SectorId = 999999,
            IndustryId = null
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Указанный сектор не найден.", badRequest.Value);
    }

    [Fact]
    public async Task UpdateMetadata_NewArchivedSectorAssignment_IsRejected_ButExistingArchivedCanBePreserved()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var archivedSector = new Sector
        {
            Id = 441,
            Name = "Archived sector",
            NormalizedName = "archived sector",
            IsArchived = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.Add(archivedSector);
        context.Stocks.AddRange(
            new Stock
            {
                Id = 442,
                Ticker = "S1",
                Name = "Stock 1",
                CommonName = "Stock 1",
                Exchange = StockExchanges.Nyse,
                CurrentPrice = 10m,
                UpdatedAt = now
            },
            new Stock
            {
                Id = 443,
                Ticker = "S2",
                Name = "Stock 2",
                CommonName = "Stock 2",
                Exchange = StockExchanges.Nyse,
                CurrentPrice = 20m,
                SectorId = archivedSector.Id,
                UpdatedAt = now
            });
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        var rejected = await controller.UpdateMetadata(442, new UpdateStockMetadataRequest
        {
            Name = "Stock 1",
            CommonName = "Stock 1",
            CurrentPrice = 11m,
            SectorId = archivedSector.Id,
            IndustryId = null
        });
        var badRequest = Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Equal("Нельзя привязать акцию к архивному сектору.", badRequest.Value);

        var preserved = await controller.UpdateMetadata(443, new UpdateStockMetadataRequest
        {
            Name = "Stock 2 updated",
            CommonName = "Stock 2",
            CurrentPrice = 21m,
            SectorId = archivedSector.Id,
            IndustryId = null
        });
        Assert.IsType<NoContentResult>(preserved);
        var preservedStock = await context.Stocks.SingleAsync(x => x.Id == 443);
        Assert.Equal(archivedSector.Id, preservedStock.SectorId);
        Assert.Null(preservedStock.IndustryId);
    }

    [Fact]
    public async Task Create_SectorAndIndustryRules_AreAppliedConsistently()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var sectorA = new Sector
        {
            Id = 451,
            Name = "Financials",
            NormalizedName = "financials",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var sectorB = new Sector
        {
            Id = 452,
            Name = "Information Technology",
            NormalizedName = "information technology",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var archivedSector = new Sector
        {
            Id = 453,
            Name = "Archived sector",
            NormalizedName = "archived sector",
            IsArchived = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var industryB = new Industry
        {
            Id = 454,
            SectorId = sectorB.Id,
            Sector = sectorB,
            Name = "Software",
            NormalizedName = "software",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.AddRange(sectorA, sectorB, archivedSector);
        context.Industries.Add(industryB);
        await context.SaveChangesAsync();

        var controller = CreateController(context);

        var directSectorCreate = await controller.Create(new Stock
        {
            Ticker = "C1",
            Name = "Create 1",
            CommonName = "Create 1",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 1m,
            SectorId = sectorA.Id
        });
        var directStock = Assert.IsType<Stock>(Assert.IsType<CreatedAtActionResult>(directSectorCreate.Result).Value);
        Assert.Equal(sectorA.Id, directStock.SectorId);
        Assert.Null(directStock.IndustryId);
        Assert.Equal(sectorA.Id, directStock.Sector?.Id);

        var conflictingCreate = await controller.Create(new Stock
        {
            Ticker = "C2",
            Name = "Create 2",
            CommonName = "Create 2",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 2m,
            SectorId = sectorA.Id,
            IndustryId = industryB.Id
        });
        var conflictingStock = Assert.IsType<Stock>(Assert.IsType<CreatedAtActionResult>(conflictingCreate.Result).Value);
        Assert.Equal(industryB.Id, conflictingStock.IndustryId);
        Assert.Equal(sectorB.Id, conflictingStock.SectorId);
        Assert.Equal(sectorB.Id, conflictingStock.Sector?.Id);

        var missingSector = await controller.Create(new Stock
        {
            Ticker = "C3",
            Name = "Create 3",
            CommonName = "Create 3",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 3m,
            SectorId = 999999
        });
        Assert.Equal("Указанный сектор не найден.", Assert.IsType<BadRequestObjectResult>(missingSector.Result).Value);

        var archivedSectorCreate = await controller.Create(new Stock
        {
            Ticker = "C4",
            Name = "Create 4",
            CommonName = "Create 4",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 4m,
            SectorId = archivedSector.Id
        });
        Assert.Equal("Нельзя привязать акцию к архивному сектору.", Assert.IsType<BadRequestObjectResult>(archivedSectorCreate.Result).Value);
    }

    [Fact]
    public async Task UpdateMetadata_BlankCommonName_FallsBackToName()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 203,
            Ticker = "IBM",
            Name = "IBM Corp",
            CommonName = "IBM",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = "International Business Machines",
            CommonName = "   ",
            CurrentPrice = 100m,
        });

        Assert.IsType<NoContentResult>(result);
        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal("International Business Machines", persisted.Name);
        Assert.Equal("International Business Machines", persisted.CommonName);
    }

    [Fact]
    public async Task LegacyPut_ReturnsGone_PerformsNoWrite()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 210,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 180m,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = controller.Update(existing.Id);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, statusResult.StatusCode);

        // The stock must not be modified
        var persisted = await context.Stocks.SingleAsync();
        Assert.Equal("Apple Inc.", persisted.Name);
        Assert.Equal("AAPL", persisted.Ticker);
        Assert.Equal(180m, persisted.CurrentPrice);
    }

    [Fact]
    public async Task LegacyPut_ReturnsGone_MessageMentionsNewEndpoints()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = controller.Update(42);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, statusResult.StatusCode);
        var message = Assert.IsType<string>(statusResult.Value);
        Assert.Contains("/metadata", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/quote", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithMarketIndexIds_PersistsJoinRows()
    {
        await using var context = CreateContext();
        context.MarketIndices.AddRange(
            new MarketIndex { Id = 1, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX", NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new MarketIndex { Id = 2, Name = "NASDAQ-100", NormalizedName = "NASDAQ-100", Code = "NDX", NormalizedCode = "NDX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            MarketIndexIds = new List<int> { 1, 2 }
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var stock = Assert.IsType<Stock>(created.Value);
        Assert.Equal(new[] { 1, 2 }, stock.MarketIndexIds);
        Assert.Equal(2, await context.StockMarketIndices.CountAsync());
    }

    [Fact]
    public async Task UpdateMetadata_WithMarketIndexIds_SyncsJoinRows()
    {
        await using var context = CreateContext();
        context.MarketIndices.AddRange(
            new MarketIndex { Id = 1, Name = "S&P 500", NormalizedName = "S&P 500", Code = "SPX", NormalizedCode = "SPX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new MarketIndex { Id = 2, Name = "NASDAQ-100", NormalizedName = "NASDAQ-100", Code = "NDX", NormalizedCode = "NDX", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new MarketIndex { Id = 3, Name = "MSCI World", NormalizedName = "MSCI WORLD", Code = "MSCIW", NormalizedCode = "MSCIW", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var stock = new Stock
        {
            Id = 300,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { StockId = 300, MarketIndexId = 1 },
                new() { StockId = 300, MarketIndexId = 2 }
            }
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(stock.Id, new UpdateStockMetadataRequest
        {
            Name = stock.Name,
            CommonName = stock.CommonName,
            CurrentPrice = stock.CurrentPrice,
            MarketIndexIds = new List<int> { 2, 3 }
        });

        Assert.IsType<NoContentResult>(result);
        var joins = await context.StockMarketIndices
            .Where(x => x.StockId == stock.Id && x.EffectiveTo == null)
            .OrderBy(x => x.MarketIndexId)
            .Select(x => x.MarketIndexId)
            .ToListAsync();
        Assert.Equal(new[] { 2, 3 }, joins);
    }

    [Fact]
    public async Task Create_UnknownMarketIndexId_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            MarketIndexIds = new List<int> { 999 }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Указан несуществующий мировой индекс.", badRequest.Value);
    }

    [Fact]
    public async Task Create_ArchivedMarketIndexId_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5,
            Name = "Archived",
            NormalizedName = "ARCHIVED",
            Code = "ARC",
            NormalizedCode = "ARC",
            IsArchived = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.Create(new Stock
        {
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            MarketIndexIds = new List<int> { 5 }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Нельзя привязать акцию к архивному мировому индексу.", badRequest.Value);
    }

    [Fact]
    public async Task UpdateMetadata_WithoutMarketIndexIds_PreservesExistingBindings()
    {
        await using var context = CreateContext();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1,
            Name = "S&P 500",
            NormalizedName = "S&P 500",
            Code = "SPX",
            NormalizedCode = "SPX",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var stock = new Stock
        {
            Id = 301,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { StockId = 301, MarketIndexId = 1 }
            }
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(stock.Id, new UpdateStockMetadataRequest
        {
            Name = "Apple Inc. Updated",
            CommonName = "Apple",
            CurrentPrice = 101m
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == stock.Id));
    }

    [Fact]
    public async Task GetById_ReturnsOnlyActiveMarketIndexIds_ExcludingFormerMemberships()
    {
        await using var context = CreateContext();
        var formerIndex = new MarketIndex
        {
            Id = 1,
            Name = "Former",
            NormalizedName = "FORMER",
            Code = "FRM",
            NormalizedCode = "FRM",
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var activeIndex = new MarketIndex
        {
            Id = 2,
            Name = "Active",
            NormalizedName = "ACTIVE",
            Code = "ACT",
            NormalizedCode = "ACT",
            SortOrder = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.MarketIndices.AddRange(formerIndex, activeIndex);
        context.Stocks.Add(new Stock
        {
            Id = 302,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { StockId = 302, MarketIndexId = 1, MarketIndex = formerIndex, EffectiveTo = DateTime.UtcNow.AddDays(-1) },
                new() { StockId = 302, MarketIndexId = 2, MarketIndex = activeIndex }
            }
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetById(302);

        var stock = Assert.IsType<Stock>(result.Value);
        Assert.Equal(new[] { 2 }, stock.MarketIndexIds);
    }

    [Fact]
    public async Task UpdateMetadata_CatalogOnlyStock_PreservesTrackingStatus()
    {
        await using var context = CreateContext();
        var existing = new Stock
        {
            Id = 303,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(existing.Id, new UpdateStockMetadataRequest
        {
            Name = "SAP SE Updated",
            CommonName = "SAP Updated",
            CurrentPrice = 101m
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(StockTrackingStatus.CatalogOnly, await context.Stocks.Select(x => x.TrackingStatus).SingleAsync());
    }

    [Fact]
    public async Task UpdateMetadata_UnchangedMarketIndexIds_DoesNotChurnMembershipHistory()
    {
        await using var context = CreateContext();
        var marketIndex = new MarketIndex
        {
            Id = 4,
            Name = "S&P 500",
            NormalizedName = "S&P 500",
            Code = "SPX",
            NormalizedCode = "SPX",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.MarketIndices.Add(marketIndex);
        context.Stocks.Add(new Stock
        {
            Id = 304,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { StockId = 304, MarketIndexId = 4, MarketIndex = marketIndex }
            }
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(304, new UpdateStockMetadataRequest
        {
            Name = "Apple Inc.",
            CommonName = "Apple",
            CurrentPrice = 100m,
            MarketIndexIds = new List<int> { 4 }
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == 304));
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == 304 && x.EffectiveTo == null));
    }

    [Fact]
    public async Task UpdateMetadata_ExistingArchivedMarketIndexBinding_IsAllowed()
    {
        await using var context = CreateContext();
        var archivedIndex = new MarketIndex
        {
            Id = 6,
            Name = "Archived",
            NormalizedName = "ARCHIVED",
            Code = "ARC",
            NormalizedCode = "ARC",
            IsArchived = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.MarketIndices.Add(archivedIndex);
        context.Stocks.Add(new Stock
        {
            Id = 305,
            Ticker = "SAP",
            Name = "SAP SE",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow,
            MarketIndices = new List<StockMarketIndex>
            {
                new() { StockId = 305, MarketIndexId = 6, MarketIndex = archivedIndex }
            }
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.UpdateMetadata(305, new UpdateStockMetadataRequest
        {
            Name = "SAP SE Updated",
            CommonName = "SAP",
            CurrentPrice = 100m,
            MarketIndexIds = new List<int> { 6 }
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.StockId == 305 && x.EffectiveTo == null));
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_UsesCurrentPriceAsEndpoint_WhenItIsNewerThanHistory()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 680,
            Ticker = "NEWEND",
            Name = "New Endpoint",
            CommonName = "New Endpoint",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 120m,
            CurrentPriceAt = now.AddMinutes(-15),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 680, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 680, Interval = "1h", Timestamp = now.AddDays(-6), Close = 105m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 680, Interval = "1h", Timestamp = now.AddHours(-2), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        Assert.Equal(100m, item.StartPrice);
        Assert.Equal(120m, item.EndPrice);
        Assert.Equal(20d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_UsesLatestHistory_WhenCurrentPriceIsOlder()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 681,
            Ticker = "OLDEND",
            Name = "Old Endpoint",
            CommonName = "Old Endpoint",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 500m,
            CurrentPriceAt = now.AddHours(-4),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 681, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-3), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 681, Interval = "1h", Timestamp = now.AddHours(-2), Close = 130m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(130m, item.EndPrice);
        Assert.Equal(30d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_MissingCurrentPriceTimestamp_DoesNotOverrideHistoryEndpoint()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 682,
            Ticker = "NOTIME",
            Name = "No Timestamp",
            CommonName = "No Timestamp",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 999m,
            CurrentPriceAt = null,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 682, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-3), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 682, Interval = "1h", Timestamp = now.AddHours(-1), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(110m, item.EndPrice);
        Assert.Equal(10d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_UsesBoundaryBaselineAtOrBeforePeriodStart()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 683,
            Ticker = "WEEKEND",
            Name = "Weekend Baseline",
            CommonName = "Weekend Baseline",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 160m,
            CurrentPriceAt = now.AddHours(-3),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 683, Interval = "1h", Timestamp = now.AddDays(-8), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 683, Interval = "1h", Timestamp = now.AddDays(-6), Close = 150m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 683, Interval = "1h", Timestamp = now.AddHours(-4), Close = 155m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(100m, item.StartPrice);
        Assert.Equal(160m, item.EndPrice);
        Assert.Equal(60d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_DoesNotUseCurrentEndpoint_WhenLatestHistoryCurrencyIsIncompatible()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 684,
            Ticker = "USDX",
            Name = "USD Endpoint",
            CommonName = "USD Endpoint",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 200m,
            CurrentPriceAt = now.AddMinutes(-5),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 684, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-3), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "USD" },
            new StockHistoricalPrice { StockId = 684, Interval = "1h", Timestamp = now.AddHours(-1), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "USD" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(110m, item.EndPrice);
        Assert.Equal(10d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_CurrentEndpointUpdateChangesComputedOrderingMetric()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.AddRange(
            new Stock
            {
                Id = 685,
                Ticker = "FAST",
                Name = "Fast",
                CommonName = "Fast",
                Exchange = StockExchanges.Frankfurt,
                CurrentPrice = 130m,
                CurrentPriceAt = now.AddMinutes(-5),
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now,
            },
            new Stock
            {
                Id = 686,
                Ticker = "SLOW",
                Name = "Slow",
                CommonName = "Slow",
                Exchange = StockExchanges.Frankfurt,
                CurrentPrice = 115m,
                CurrentPriceAt = now.AddMinutes(-5),
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now,
            });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 685, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 685, Interval = "1h", Timestamp = now.AddHours(-3), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 686, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 686, Interval = "1h", Timestamp = now.AddHours(-3), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var ordered = response.Items
            .Where(x => x.ChangePercent.HasValue)
            .OrderByDescending(x => x.ChangePercent!.Value)
            .Select(x => x.StockId)
            .ToArray();
        Assert.Equal(new[] { 685, 686 }, ordered);
    }

    [Fact]
    public async Task GetCatalogPerformance_24h_WithSparseIntradayHistory_ReturnsInsufficientData()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 701,
            Ticker = "SNAP24",
            Name = "Snapshot 24h",
            CommonName = "Snapshot 24h",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 210m,
            CurrentPriceChange = 10m,
            CurrentPriceChangePercent = 5m,
            CurrentPriceAt = DateTime.UtcNow,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        // One 10m point is insufficient for the history-only path.
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = 701,
            Timestamp = DateTime.UtcNow.AddHours(-1),
            Interval = "10m",
            Close = 208m,
            QuoteUnitMultiplier = 1m,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("24h");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public async Task GetCatalogPerformance_24h_WithoutHistoryAndSnapshot_ReturnsInsufficientData()
    {
        await using var context = CreateContext();
        context.Stocks.Add(new Stock
        {
            Id = 702,
            Ticker = "NOSNAP",
            Name = "No snapshot",
            CommonName = "No snapshot",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("24h");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public void CatalogPerformanceHistoryQuery_PomeloCannotTranslate_OrderByAfterProjection_ButCanTranslate_WhenOrderedBeforeProjection()
    {
        using var context = CreateMySqlContext();
        var now = DateTime.UtcNow;
        var stockIds = new[] { 1 };

        var nonTranslatableShape = context.StockHistoricalPrices
            .AsNoTracking()
            .Where(x => stockIds.Contains(x.StockId) && x.Interval == "1h" && x.Timestamp >= now.AddDays(-14))
            .Select(x => new CatalogPerformanceHistorySqlPoint(
                x.StockId,
                x.Timestamp,
                x.Close,
                x.QuoteUnitMultiplier,
                x.NormalizedQuoteCurrency))
            .OrderBy(x => x.StockId)
            .ThenBy(x => x.Timestamp);

        Assert.Throws<InvalidOperationException>(() => nonTranslatableShape.ToQueryString());

        var translatableShape = context.StockHistoricalPrices
            .AsNoTracking()
            .Where(x => stockIds.Contains(x.StockId) && x.Interval == "1h" && x.Timestamp >= now.AddDays(-14))
            .OrderBy(x => x.StockId)
            .ThenBy(x => x.Timestamp)
            .Select(x => new CatalogPerformanceHistorySqlPoint(
                x.StockId,
                x.Timestamp,
                x.Close,
                x.QuoteUnitMultiplier,
                x.NormalizedQuoteCurrency));

        var sql = translatableShape.ToQueryString();
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogPerformanceHistoryQuery_PomeloTranslates_GroupedBoundaryAndLatestShapes()
    {
        using var context = CreateMySqlContext();
        var now = DateTime.UtcNow;
        var stockIds = new[] { 1, 2 };
        var interval = "1h";
        var boundary = now.AddDays(-7);
        var queryFrom = boundary.AddDays(-7);

        var baseQuery = context.StockHistoricalPrices
            .AsNoTracking()
            .Where(x =>
                stockIds.Contains(x.StockId) &&
                x.Interval == interval &&
                x.Timestamp >= queryFrom &&
                x.Timestamp <= now &&
                x.Close > 0m &&
                x.QuoteUnitMultiplier > 0m);

        var latestByStock = baseQuery
            .GroupBy(x => x.StockId)
            .Select(g => new { StockId = g.Key, Timestamp = g.Max(x => x.Timestamp) });

        var baselineByStock = baseQuery
            .Where(x => x.Timestamp <= boundary)
            .GroupBy(x => x.StockId)
            .Select(g => new { StockId = g.Key, Timestamp = g.Max(x => x.Timestamp) });

        var latestRows = from point in baseQuery
                         join key in latestByStock
                             on new { point.StockId, point.Timestamp } equals new { key.StockId, key.Timestamp }
                         select new { point.StockId, point.Timestamp, point.Close, point.QuoteUnitMultiplier };

        var baselineRows = from point in baseQuery
                           join key in baselineByStock
                               on new { point.StockId, point.Timestamp } equals new { key.StockId, key.Timestamp }
                           select new { point.StockId, point.Timestamp, point.Close, point.QuoteUnitMultiplier };

        var latestSql = latestRows.ToQueryString();
        var baselineSql = baselineRows.ToQueryString();

        Assert.Contains("GROUP BY", latestSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", baselineSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JOIN", latestSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JOIN", baselineSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_DoesNotUseCurrentEndpoint_WhenCurrentPriceIsStale()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 703,
            Ticker = "STALE",
            Name = "Stale Endpoint",
            CommonName = "Stale Endpoint",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 500m,
            CurrentPriceAt = now.AddHours(-25),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 703, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 703, Interval = "1h", Timestamp = now.AddMinutes(-30), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(110m, item.EndPrice);
        Assert.Equal(10d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_DoesNotUseCurrentEndpoint_WhenCurrentPriceTimestampIsInFuture()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 704,
            Ticker = "FUTURE",
            Name = "Future Endpoint",
            CommonName = "Future Endpoint",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 500m,
            CurrentPriceAt = now.AddMinutes(15),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 704, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 704, Interval = "1h", Timestamp = now.AddMinutes(-30), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(110m, item.EndPrice);
        Assert.Equal(10d, item.ChangePercent!.Value, 6);
    }

    [Fact]
    public async Task GetCatalogPerformance_1w_DoesNotUseCurrentEndpoint_WhenCurrentPriceIsNonPositive()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 705,
            Ticker = "NONPOS",
            Name = "Non Positive Endpoint",
            CommonName = "Non Positive Endpoint",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 0m,
            CurrentPriceAt = now.AddMinutes(-10),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });

        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = 705, Interval = "1h", Timestamp = now.AddDays(-7).AddHours(-2), Close = 100m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" },
            new StockHistoricalPrice { StockId = 705, Interval = "1h", Timestamp = now.AddMinutes(-30), Close = 110m, QuoteUnitMultiplier = 1m, NormalizedQuoteCurrency = "EUR" });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance("1w");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(110m, item.EndPrice);
        Assert.Equal(10d, item.ChangePercent!.Value, 6);
    }

    [Theory]
    [InlineData("today", "10m", -3, -1)]
    [InlineData("24h", "10m", -23, -1)]
    [InlineData("1w", "1h", -6, -1)]
    [InlineData("1m", "1d", -20, -1)]
    public async Task GetCatalogPerformance_SupportedRanges_ReturnSuccessfulResponse(
        string range,
        string interval,
        int startOffsetHours,
        int endOffsetHours)
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 706,
            Ticker = "RANGE",
            Name = "Range",
            CommonName = "Range",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 112m,
            CurrentPriceAt = now.AddMinutes(-5),
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now,
        });
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice
            {
                StockId = 706,
                Interval = interval,
                Timestamp = now.AddHours(startOffsetHours),
                Close = 100m,
                QuoteUnitMultiplier = 1m,
                NormalizedQuoteCurrency = "EUR",
            },
            new StockHistoricalPrice
            {
                StockId = 706,
                Interval = interval,
                Timestamp = now.AddHours(endOffsetHours),
                Close = 110m,
                QuoteUnitMultiplier = 1m,
                NormalizedQuoteCurrency = "EUR",
            });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetCatalogPerformance(range);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StockCatalogPerformanceResponse>(ok.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        Assert.NotNull(item.ChangePercent);
    }


    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AppDbContext(options);
    }

    private static AppDbContext CreateMySqlContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                Environment.GetEnvironmentVariable("FINANCEAPP_TEST_MYSQL_CONNECTION")
                    ?? "Server=example.invalid;Port=3306;Database=financeapp",
                new MariaDbServerVersion(new Version(10, 5, 23)))
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<AppDbContext> CreateSqliteRelationalContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static StocksController CreateController(AppDbContext context, IStockHistoryService? stockHistoryService = null)
    {
        return new StocksController(
            context,
            stockHistoryService ?? new StubStockHistoryService(),
            new StockPerformanceCalculationService(context, TimeProvider.System),
            new StockQuoteSnapshotPersistenceService(
                context,
                TimeProvider.System,
                NullLogger<StockQuoteSnapshotPersistenceService>.Instance),
            NullLogger<StocksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private sealed class StubStockHistoryService : IStockHistoryService
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

    private sealed class RecordingStockHistoryService : IStockHistoryService
    {
        private readonly List<(int StockId, string Range)> _getHistoryCalls = [];
        private readonly List<(int StockId, string Exchange)> _refreshHistoryCalls = [];
        private readonly List<(int StockId, string? Candidate)> _validateCalls = [];
        private readonly List<(int StockId, string? Candidate)> _hardResetCalls = [];

        public Func<Stock, string, StockHistoryResponse>? HistoryResponseFactory { get; init; }
        public Func<Stock, StockHistoryRefreshResponse>? RefreshResponseFactory { get; init; }
        public Func<Stock, string?, StockHistoryRepairDiagnosticsResponse>? RoutingDiagnosticsFactory { get; init; }
        public Func<Stock, string?, StockHistoryRepairDiagnosticsResponse>? ValidationDiagnosticsFactory { get; init; }
        public Func<Stock, string?, StockHistoryRepairDiagnosticsResponse>? HardResetDiagnosticsFactory { get; init; }

        public IReadOnlyList<(int StockId, string Range)> GetHistoryCalls => _getHistoryCalls;
        public IReadOnlyList<(int StockId, string Exchange)> RefreshHistoryCalls => _refreshHistoryCalls;
        public IReadOnlyList<(int StockId, string? Candidate)> ValidateCalls => _validateCalls;
        public IReadOnlyList<(int StockId, string? Candidate)> HardResetCalls => _hardResetCalls;

        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
        {
            _getHistoryCalls.Add((stock.Id, range));
            return Task.FromResult(HistoryResponseFactory?.Invoke(stock, range) ?? new StockHistoryResponse
            {
                Range = range,
                Interval = "1d"
            });
        }

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
        {
            _refreshHistoryCalls.Add((stock.Id, stock.Exchange));
            return Task.FromResult(RefreshResponseFactory?.Invoke(stock) ?? new StockHistoryRefreshResponse { StockId = stock.Id });
        }

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StockHistoryRepairDiagnosticsResponse> GetRepairDiagnosticsAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.FromResult(RoutingDiagnosticsFactory?.Invoke(stock, null) ?? new StockHistoryRepairDiagnosticsResponse
            {
                StockId = stock.Id,
                Ticker = stock.Ticker,
                Exchange = stock.Exchange,
                Name = stock.Name,
                ConfiguredProviderSymbol = stock.ProviderSymbol,
                EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
                ResultBucket = "success",
            });

        public Task<StockHistoryRepairDiagnosticsResponse> ValidateProviderSymbolAsync(Stock stock, string? candidateProviderSymbol, CancellationToken cancellationToken = default)
        {
            _validateCalls.Add((stock.Id, candidateProviderSymbol));
            return Task.FromResult(ValidationDiagnosticsFactory?.Invoke(stock, candidateProviderSymbol) ?? new StockHistoryRepairDiagnosticsResponse
            {
                StockId = stock.Id,
                Ticker = stock.Ticker,
                Exchange = stock.Exchange,
                Name = stock.Name,
                ConfiguredProviderSymbol = stock.ProviderSymbol,
                EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
                CandidateOverrideSymbol = candidateProviderSymbol,
                ResultBucket = "success",
            });
        }

        public Task<StockHistoryRepairDiagnosticsResponse> HardResetHistoryAsync(Stock stock, string? candidateProviderSymbol, CancellationToken cancellationToken = default)
        {
            _hardResetCalls.Add((stock.Id, candidateProviderSymbol));
            return Task.FromResult(HardResetDiagnosticsFactory?.Invoke(stock, candidateProviderSymbol) ?? new StockHistoryRepairDiagnosticsResponse
            {
                StockId = stock.Id,
                Ticker = stock.Ticker,
                Exchange = stock.Exchange,
                Name = stock.Name,
                ConfiguredProviderSymbol = stock.ProviderSymbol,
                EffectiveProviderSymbol = stock.ProviderSymbol ?? stock.Ticker,
                CandidateOverrideSymbol = candidateProviderSymbol,
                ResultBucket = "success",
                ResetPerformed = true,
            });
        }
    }

    private readonly record struct CatalogPerformanceHistorySqlPoint(
        int StockId,
        DateTime Timestamp,
        decimal Close,
        decimal QuoteUnitMultiplier,
        string? NormalizedQuoteCurrency);
}
