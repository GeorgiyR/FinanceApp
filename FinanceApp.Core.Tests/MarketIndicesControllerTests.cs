using System.Collections.Concurrent;
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

public class MarketIndicesControllerTests
{
    [Fact]
    public async Task Seed_ContainsAll27RequiredIndices()
    {
        await using var context = await CreateSqliteContextAsync();

        var marketIndices = await context.MarketIndices
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(27, marketIndices.Count);
        Assert.Equal(new[] { "DJIA", "SPX", "COMP", "NDX", "RUT" }, marketIndices.Take(5).Select(x => x.Code));
        Assert.Contains(marketIndices, x => x.Code == "MSCIACWI" && x.CountryOrRegion == "Global");
    }

    [Fact]
    public async Task Crud_Archive_And_Restore_Work()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var createResult = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Test Index",
            Code = "TIDX",
            CountryOrRegion = "Testland",
            Description = "Описание",
            SortOrder = 999
        });

        var created = Assert.IsType<ObjectResult>(createResult.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var dto = Assert.IsType<MarketIndexDto>(created.Value);
        Assert.Equal("TIDX", dto.Code);

        var updateResult = await controller.UpdateMarketIndex(dto.Id, new UpsertMarketIndexRequest
        {
            Name = "Test Index Updated",
            Code = "TIDX2",
            CountryOrRegion = "Updated",
            Description = "Обновлено",
            SortOrder = 1000
        });
        var updated = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<MarketIndexDto>(updated.Value);
        Assert.Equal("TIDX2", updatedDto.Code);

        var archiveResult = await controller.ArchiveMarketIndex(dto.Id);
        var archived = Assert.IsType<OkObjectResult>(archiveResult.Result);
        Assert.True(Assert.IsType<MarketIndexDto>(archived.Value).IsArchived);

        var restoreResult = await controller.RestoreMarketIndex(dto.Id);
        var restored = Assert.IsType<OkObjectResult>(restoreResult.Result);
        Assert.False(Assert.IsType<MarketIndexDto>(restored.Value).IsArchived);

        var deleteResult = await controller.DeleteMarketIndex(dto.Id);
        Assert.IsType<NoContentResult>(deleteResult);
        Assert.False(await context.MarketIndices.AnyAsync(x => x.Id == dto.Id));
    }

    [Fact]
    public async Task Create_DuplicateCode_ReturnsConflict()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var result = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Duplicate S&P",
            Code = "SPX",
            SortOrder = 10
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("Индекс с таким кодом уже существует.", conflict.Value);
    }

    [Fact]
    public async Task Delete_UsedMarketIndex_ReturnsConflict()
    {
        await using var context = await CreateSqliteContextAsync();
        var stock = new Stock
        {
            Id = 500,
            Ticker = "AAPL",
            Name = "Apple Inc.",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            CurrentPrice = 100m,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 500, MarketIndexId = 1 });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.DeleteMarketIndex(1);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("Нельзя удалить индекс, к которому привязаны акции.", conflict.Value);
    }

    [Fact]
    public async Task GetAll_ExcludeArchivedByDefault()
    {
        await using var context = await CreateSqliteContextAsync();
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 1000,
            Name = "Archived Test",
            NormalizedName = "ARCHIVED TEST",
            Code = "ATST",
            NormalizedCode = "ATST",
            IsArchived = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<MarketIndexDto>>(ok.Value).ToList();

        Assert.DoesNotContain(items, x => x.Code == "ATST");
    }

    [Fact]
    public async Task Create_WithProviderSymbol_SavesAndReturnsIt()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var result = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Test Index With Symbol",
            Code = "TIDXS",
            ProviderSymbol = "^TIDX",
            SortOrder = 999
        });

        var created = Assert.IsType<ObjectResult>(result.Result);
        var dto = Assert.IsType<MarketIndexDto>(created.Value);
        Assert.Equal("^TIDX", dto.ProviderSymbol);
    }

    [Fact]
    public async Task Update_ChangingProviderSymbol_ClearsOldHistory()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        context.MarketIndices.Add(new MarketIndex
        {
            Id = 900,
            Name = "Symbol Change Test",
            NormalizedName = "SYMBOL CHANGE TEST",
            Code = "SCTST",
            NormalizedCode = "SCTST",
            ProviderSymbol = "^OLD",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        context.MarketIndexHistoricalPrices.Add(new MarketIndexHistoricalPrice
        {
            MarketIndexId = 900,
            Timestamp = now.AddDays(-1),
            Interval = "1d",
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            ProviderSymbol = "^OLD"
        });
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.MarketIndexHistoricalPrices.CountAsync(x => x.MarketIndexId == 900));

        var controller = CreateController(context);
        await controller.UpdateMarketIndex(900, new UpsertMarketIndexRequest
        {
            Name = "Symbol Change Test",
            Code = "SCTST",
            ProviderSymbol = "^NEW",
            SortOrder = 0
        });

        Assert.Equal(0, await context.MarketIndexHistoricalPrices.CountAsync(x => x.MarketIndexId == 900));
    }

    [Fact]
    public async Task Update_SameProviderSymbol_PreservesHistory()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        context.MarketIndices.Add(new MarketIndex
        {
            Id = 901,
            Name = "Preserve History Test",
            NormalizedName = "PRESERVE HISTORY TEST",
            Code = "PHTST",
            NormalizedCode = "PHTST",
            ProviderSymbol = "^SAME",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        context.MarketIndexHistoricalPrices.Add(new MarketIndexHistoricalPrice
        {
            MarketIndexId = 901,
            Timestamp = now.AddDays(-1),
            Interval = "1d",
            Open = 100m,
            High = 110m,
            Low = 95m,
            Close = 105m,
            ProviderSymbol = "^SAME"
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        await controller.UpdateMarketIndex(901, new UpsertMarketIndexRequest
        {
            Name = "Preserve History Test Updated",
            Code = "PHTST",
            ProviderSymbol = "^SAME",
            SortOrder = 0
        });

        Assert.Equal(1, await context.MarketIndexHistoricalPrices.CountAsync(x => x.MarketIndexId == 901));
    }

    [Fact]
    public async Task GetConstituents_ExposesPersistedDelayedQuoteMetadata()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        context.Stocks.Add(new Stock
        {
            Id = 950,
            Ticker = "MTE.F",
            Name = "Seagate",
            CommonName = "Seagate",
            Exchange = StockExchanges.Frankfurt,
            CurrentPrice = 752m,
            CurrentPriceChange = -52m,
            CurrentPriceChangePercent = -6.47m,
            CurrentPriceAt = new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc),
            CurrentPriceIsDelayed = true,
            CurrentPriceDelayWarning = "Котировка задержана",
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = 950,
            MarketIndexId = 1,
            EffectiveFrom = now.AddDays(-1),
            ImportedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituents(1);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<IndexConstituentsResponse>(ok.Value);
        var constituent = Assert.Single(response.Constituents, x => x.StockId == 950);
        Assert.True(constituent.CurrentPriceIsDelayed);
        Assert.Equal("Котировка задержана", constituent.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task GetHistory_NoProviderSymbol_Returns422()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        context.MarketIndices.Add(new MarketIndex
        {
            Id = 902,
            Name = "No Symbol Index",
            NormalizedName = "NO SYMBOL INDEX",
            Code = "NSIDX",
            NormalizedCode = "NSIDX",
            ProviderSymbol = null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetHistory(902);

        Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetHistory_InvalidRange_Returns400()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        context.MarketIndices.Add(new MarketIndex
        {
            Id = 903,
            Name = "Range Test Index",
            NormalizedName = "RANGE TEST INDEX",
            Code = "RTIDX",
            NormalizedCode = "RTIDX",
            ProviderSymbol = "^TEST",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetHistory(903, range: "invalid_range");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetHistory_UnknownIndex_Returns404()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);
        var result = await controller.GetHistory(99999);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Seed_ProviderSymbols_AreCorrectForKnownIndices()
    {
        await using var context = await CreateSqliteContextAsync();
        var indices = await context.MarketIndices
            .OrderBy(x => x.Id)
            .ToListAsync();

        var byCode = indices.ToDictionary(x => x.Code, x => x.ProviderSymbol);

        Assert.Equal("^DJI",      byCode["DJIA"]);
        Assert.Equal("^GSPC",     byCode["SPX"]);
        Assert.Equal("^IXIC",     byCode["COMP"]);
        Assert.Equal("^NDX",      byCode["NDX"]);
        Assert.Equal("^RUT",      byCode["RUT"]);
        Assert.Equal("^FTSE",     byCode["UKX"]);
        Assert.Equal("^GDAXI",    byCode["DAX"]);
        Assert.Equal("^N225",     byCode["NKY"]);
        Assert.Equal("^HSI",      byCode["HSI"]);
        Assert.Equal("000300.SS", byCode["CSI300"]);
        Assert.Equal("000001.SS", byCode["SHCOMP"]);
        Assert.Equal("FTSEMIB.MI", byCode["FTSEMIB"]);
        Assert.Equal("^BVSP",     byCode["IBOV"]);
        // MSCI indices intentionally have no direct public symbol
        Assert.Null(byCode["MSCIW"]);
        Assert.Null(byCode["MSCIEM"]);
        Assert.Null(byCode["MSCIACWI"]);
    }

    [Fact]
    public async Task Seed_ProviderSymbols_AreUnique_WhereNotNull()
    {
        await using var context = await CreateSqliteContextAsync();
        var symbols = await context.MarketIndices
            .Where(x => x.ProviderSymbol != null)
            .Select(x => x.ProviderSymbol!)
            .ToListAsync();

        Assert.Equal(symbols.Count, symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("5y")]
    [InlineData("3y")]
    [InlineData("1y")]
    [InlineData("6m")]
    [InlineData("3m")]
    [InlineData("1m")]
    [InlineData("1w")]
    [InlineData("24h")]
    [InlineData("today")]
    public void RangeValidation_ValidRanges_DoNotThrow(string range)
    {
        var normalized = MarketIndexHistoryService.NormalizeRange(range);
        Assert.Equal(range, normalized);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData("10y")]
    [InlineData("ALL")]
    public void RangeValidation_InvalidRanges_Throw(string range)
    {
        Assert.Throws<ArgumentException>(() => MarketIndexHistoryService.NormalizeRange(range));
    }

    [Theory]
    [InlineData("^DJI")]
    [InlineData("^GSPC")]
    [InlineData("000300.SS")]
    [InlineData("FTSEMIB.MI")]
    [InlineData("^BVSP")]
    public void SymbolValidation_ValidSymbols_ReturnsTrue(string symbol)
    {
        Assert.True(MarketIndexHistoryService.IsValidProviderSymbol(symbol));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("symbol with spaces")]
    [InlineData("symbol;DROP TABLE")]
    [InlineData("https://evil.com/path")]
    public void SymbolValidation_InvalidSymbols_ReturnsFalse(string? symbol)
    {
        Assert.False(MarketIndexHistoryService.IsValidProviderSymbol(symbol));
    }

    [Fact]
    public async Task GetConstituentHistory_CurrentCatalogOnlyMember_ReturnsHistoryWithoutTrackingMutation()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        var stock = new Stock
        {
            Id = 5901,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        };
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = stock.Id, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var service = new TestStockHistoryReadService();
        var controller = CreateController(context, stockHistoryService: service);
        var result = await controller.GetConstituentHistory(1, stock.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockHistoryResponse>(ok.Value);
        Assert.Equal("1y", payload.Range);
        Assert.Single(service.GetHistoryCalls);
        Assert.Equal((stock.Id, "1y"), service.GetHistoryCalls[0]);

        var persisted = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(StockTrackingStatus.CatalogOnly, persisted.TrackingStatus);
    }

    [Fact]
    public async Task RefreshConstituents_SameIsinDifferentListings_CreatesDistinctStocks()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5906,
            Name = "Listing Identity Test",
            NormalizedName = "LISTING IDENTITY TEST",
            Code = "LIT5906",
            NormalizedCode = "LIT5906",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("SAP.DE", "SAP", "SAP SE Frankfurt", StockExchanges.Frankfurt, "DE0007164600"),
                new IndexConstituentEntry("SAP", "SAP", "SAP ADR", StockExchanges.Nyse, "DE0007164600")
            ]));

        var controller = CreateController(context, provider: provider);
        var result = await controller.RefreshConstituents(5906);

        Assert.IsType<OkObjectResult>(result.Result);

        var stocks = await context.Stocks
            .OrderBy(x => x.Exchange)
            .ThenBy(x => x.ProviderSymbol)
            .ToListAsync();

        Assert.Equal(2, stocks.Count);
        Assert.All(stocks, stock => Assert.Equal("DE0007164600", stock.Isin));
        Assert.Contains(stocks, stock => stock.Exchange == StockExchanges.Frankfurt && stock.ProviderSymbol == "SAP.DE");
        Assert.Contains(stocks, stock => stock.Exchange == StockExchanges.Nyse && stock.ProviderSymbol == "SAP");
    }

    [Fact]
    public async Task RefreshConstituents_MatchingListing_BackfillsIsinWithoutCreatingDuplicate()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5907,
            Name = "Backfill Test",
            NormalizedName = "BACKFILL TEST",
            Code = "BFT5907",
            NormalizedCode = "BFT5907",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59071,
            Ticker = "SAP",
            Name = "SAP Existing",
            CommonName = "SAP Existing",
            Exchange = StockExchanges.Frankfurt,
            ProviderSymbol = "SAP.DE",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [new IndexConstituentEntry("SAP.DE", "SAP", "SAP Updated", StockExchanges.Frankfurt, "DE0007164600")]));

        var controller = CreateController(context, provider: provider);
        var result = await controller.RefreshConstituents(5907);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(await context.Stocks.ToListAsync());

        var stock = await context.Stocks.SingleAsync();
        Assert.Equal("DE0007164600", stock.Isin);
        Assert.Equal("SAP Updated", stock.Name);
    }

    [Fact]
    public async Task RefreshConstituents_MapsSectorAndWkn_WithoutInventingIndustry()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5908,
            Name = "Sector Test",
            NormalizedName = "SECTOR TEST",
            Code = "SEC5908",
            NormalizedCode = "SEC5908",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, "US0378331005", "865985", "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5908);
        Assert.IsType<OkObjectResult>(refresh.Result);

        var stock = await context.Stocks
            .Include(x => x.Sector)
            .SingleAsync();
        Assert.Equal("865985", stock.Wkn);
        Assert.NotNull(stock.Sector);
        Assert.Equal("Information Technology", stock.Sector!.Name);
        Assert.Null(stock.IndustryId);
    }

    [Fact]
    public async Task RefreshConstituents_BlankIdentifiers_DoNotOverwriteExistingValues()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5909,
            Name = "Identifier Preserve Test",
            NormalizedName = "IDENTIFIER PRESERVE TEST",
            Code = "IDP5909",
            NormalizedCode = "IDP5909",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59091,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nasdaq,
            ProviderSymbol = "AAPL",
            Isin = "US0378331005",
            Wkn = "865985",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("AAPL", "AAPL", "Apple Updated", StockExchanges.Nasdaq, null, null, "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5909);
        Assert.IsType<OkObjectResult>(refresh.Result);

        var stock = await context.Stocks.SingleAsync();
        Assert.Equal("US0378331005", stock.Isin);
        Assert.Equal("865985", stock.Wkn);
    }

    [Fact]
    public async Task RefreshConstituents_PropagatesSectorByValidIsin_ToExistingDistinctListings()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5913,
            Name = "ISIN Propagation Test",
            NormalizedName = "ISIN PROPAGATION TEST",
            Code = "IPT5913",
            NormalizedCode = "IPT5913",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.AddRange(
            new Stock
            {
                Id = 59131,
                Ticker = "AAPL",
                Name = "Apple NYSE Tracked",
                CommonName = "Apple NYSE Tracked",
                Exchange = StockExchanges.Nyse,
                ProviderSymbol = "AAPL.NY",
                Isin = "US0378331005",
                TrackingStatus = StockTrackingStatus.Tracked,
                UpdatedAt = now
            },
            new Stock
            {
                Id = 59132,
                Ticker = "APC",
                Name = "Apple Frankfurt",
                CommonName = "Apple Frankfurt",
                Exchange = StockExchanges.Frankfurt,
                ProviderSymbol = "APC",
                Isin = "US0378331005",
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now
            });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, "US0378331005", "865985", "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5913);
        Assert.IsType<OkObjectResult>(refresh.Result);

        var stocks = await context.Stocks
            .Include(x => x.Sector)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(3, stocks.Count);

        var imported = Assert.Single(stocks, x => x.Exchange == StockExchanges.Nasdaq && x.ProviderSymbol == "AAPL");
        Assert.Equal("US0378331005", imported.Isin);
        Assert.Equal("Information Technology", imported.Sector?.Name);

        var nyse = Assert.Single(stocks, x => x.Id == 59131);
        Assert.Equal(StockTrackingStatus.Tracked, nyse.TrackingStatus);
        Assert.Equal(StockExchanges.Nyse, nyse.Exchange);
        Assert.Equal("AAPL.NY", nyse.ProviderSymbol);
        Assert.Equal("Information Technology", nyse.Sector?.Name);

        var frankfurt = Assert.Single(stocks, x => x.Id == 59132);
        Assert.Equal(StockExchanges.Frankfurt, frankfurt.Exchange);
        Assert.Equal("APC", frankfurt.ProviderSymbol);
        Assert.Equal("Information Technology", frankfurt.Sector?.Name);

        var activeMemberships = await context.StockMarketIndices
            .Where(x => x.MarketIndexId == 5913 && x.EffectiveTo == null)
            .ToListAsync();
        Assert.Single(activeMemberships);
        Assert.Equal(imported.Id, activeMemberships[0].StockId);
    }

    [Fact]
    public async Task RefreshConstituents_SectorPropagation_ConflictingSectorsAreSkippedAndCounted()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        var informationTechnology = new Sector
        {
            Id = 591401,
            Name = "Information Technology",
            NormalizedName = "INFORMATION TECHNOLOGY",
            SortOrder = 0,
            IsArchived = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var healthcare = new Sector
        {
            Id = 591402,
            Name = "Health Care",
            NormalizedName = "HEALTH CARE",
            SortOrder = 0,
            IsArchived = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.AddRange(informationTechnology, healthcare);
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5914,
            Name = "ISIN Conflict Test",
            NormalizedName = "ISIN CONFLICT TEST",
            Code = "ICT5914",
            NormalizedCode = "ICT5914",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.AddRange(
            new Stock
            {
                Id = 59141,
                Ticker = "MSFT",
                Name = "Microsoft NYSE Tracked",
                CommonName = "Microsoft NYSE Tracked",
                Exchange = StockExchanges.Nyse,
                ProviderSymbol = "MSFT.NY",
                Isin = "US5949181045",
                SectorId = healthcare.Id,
                TrackingStatus = StockTrackingStatus.Tracked,
                UpdatedAt = now
            },
            new Stock
            {
                Id = 59142,
                Ticker = "MSF",
                Name = "Microsoft Frankfurt",
                CommonName = "Microsoft Frankfurt",
                Exchange = StockExchanges.Frankfurt,
                ProviderSymbol = "MSF",
                Isin = "US5949181045",
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now
            });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("MSFT", "MSFT", "Microsoft Corporation", StockExchanges.Nasdaq, "US5949181045", "870747", "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5914);
        var ok = Assert.IsType<OkObjectResult>(refresh.Result);
        var payload = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);
        Assert.Equal(1, payload.Conflicts);

        var nyse = await context.Stocks.Include(x => x.Sector).SingleAsync(x => x.Id == 59141);
        Assert.Equal("Health Care", nyse.Sector?.Name);

        var frankfurt = await context.Stocks.Include(x => x.Sector).SingleAsync(x => x.Id == 59142);
        Assert.Null(frankfurt.SectorId);
    }

    [Fact]
    public async Task RefreshConstituents_WithIndustryClassification_DoesNotClearOrOverwriteIndustry()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        var informationTechnology = new Sector
        {
            Id = 591501,
            Name = "Information Technology",
            NormalizedName = "INFORMATION TECHNOLOGY",
            SortOrder = 0,
            IsArchived = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var healthcare = new Sector
        {
            Id = 591502,
            Name = "Health Care",
            NormalizedName = "HEALTH CARE",
            SortOrder = 0,
            IsArchived = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var pharmaceuticals = new Industry
        {
            Id = 591503,
            Name = "Pharmaceuticals",
            NormalizedName = "PHARMACEUTICALS",
            SectorId = healthcare.Id,
            SortOrder = 0,
            IsArchived = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Sectors.AddRange(informationTechnology, healthcare);
        context.Industries.Add(pharmaceuticals);
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5915,
            Name = "Industry Preserve Test",
            NormalizedName = "INDUSTRY PRESERVE TEST",
            Code = "IPR5915",
            NormalizedCode = "IPR5915",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59151,
            Ticker = "PFE",
            Name = "Pfizer Existing",
            CommonName = "Pfizer Existing",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "PFE",
            Isin = "US7170811035",
            SectorId = healthcare.Id,
            IndustryId = pharmaceuticals.Id,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("PFE", "PFE", "Pfizer Updated", StockExchanges.Nyse, "US7170811035", null, "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5915);
        Assert.IsType<OkObjectResult>(refresh.Result);

        var stock = await context.Stocks
            .Include(x => x.Sector)
            .Include(x => x.Industry)
            .SingleAsync(x => x.Id == 59151);
        Assert.Equal(pharmaceuticals.Id, stock.IndustryId);
        Assert.Equal(healthcare.Id, stock.SectorId);
        Assert.Equal("Health Care", stock.Sector?.Name);
    }

    [Fact]
    public async Task RefreshConstituents_InvalidOrMissingIsin_DoesNotPropagateByTickerOnly()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5916,
            Name = "Ticker Only Test",
            NormalizedName = "TICKER ONLY TEST",
            Code = "TOK5916",
            NormalizedCode = "TOK5916",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59161,
            Ticker = "AAPL",
            Name = "Apple NYSE Existing",
            CommonName = "Apple NYSE Existing",
            Exchange = StockExchanges.Nyse,
            ProviderSymbol = "AAPL.NY",
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("AAPL", "AAPL", "Apple Invalid ISIN", StockExchanges.Nasdaq, "INVALID-ISIN", null, "Information Technology"),
                new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, "US0378331005", null, "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5916);
        var ok = Assert.IsType<OkObjectResult>(refresh.Result);
        var payload = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);
        Assert.Equal(1, payload.Conflicts);

        var nyse = await context.Stocks.Include(x => x.Sector).SingleAsync(x => x.Id == 59161);
        Assert.Null(nyse.SectorId);

        var nasdaq = await context.Stocks.Include(x => x.Sector).SingleAsync(x => x.Exchange == StockExchanges.Nasdaq && x.ProviderSymbol == "AAPL");
        Assert.Equal("US0378331005", nasdaq.Isin);
        Assert.Equal("Information Technology", nasdaq.Sector?.Name);
    }

    [Fact]
    public async Task RefreshConstituents_SectorPropagation_IsIdempotentAcrossRuns()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        var secondRun = now.AddMinutes(5);
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5917,
            Name = "Propagation Idempotency Test",
            NormalizedName = "PROPAGATION IDEMPOTENCY TEST",
            Code = "PIT5917",
            NormalizedCode = "PIT5917",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59171,
            Ticker = "MSF",
            Name = "Microsoft Frankfurt Existing",
            CommonName = "Microsoft Frankfurt Existing",
            Exchange = StockExchanges.Frankfurt,
            ProviderSymbol = "MSF",
            Isin = "US5949181045",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        IndexConstituentEntry[] entries =
        [
            new IndexConstituentEntry("MSFT", "MSFT", "Microsoft Corporation", StockExchanges.Nasdaq, "US5949181045", "870747", "Information Technology"),
        ];
        var firstProvider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            entries));
        var secondProvider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            secondRun,
            entries));

        var firstController = CreateController(context, provider: firstProvider);
        var secondController = CreateController(context, provider: secondProvider);

        Assert.IsType<OkObjectResult>((await firstController.RefreshConstituents(5917)).Result);
        var firstTarget = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == 59171);
        Assert.NotNull(firstTarget.SectorId);
        Assert.Equal(now, firstTarget.UpdatedAt);

        var secondResult = await secondController.RefreshConstituents(5917);
        var secondOk = Assert.IsType<OkObjectResult>(secondResult.Result);
        var secondPayload = Assert.IsType<IndexConstituentsRefreshResponse>(secondOk.Value);
        Assert.Equal(0, secondPayload.Updated);

        var secondTarget = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == 59171);
        Assert.Equal(firstTarget.SectorId, secondTarget.SectorId);
        Assert.Equal(now, secondTarget.UpdatedAt);
        Assert.Equal(2, await context.Stocks.CountAsync());
        Assert.Equal(1, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 5917 && x.EffectiveTo == null));
    }

    [Fact]
    public async Task RefreshConstituents_PartialInput_DoesNotCloseCurrentMemberships()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5910,
            Name = "Partial Input Test",
            NormalizedName = "PARTIAL INPUT TEST",
            Code = "PRT5910",
            NormalizedCode = "PRT5910",
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59101,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nasdaq,
            ProviderSymbol = "MSFT",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = 59101,
            MarketIndexId = 5910,
            EffectiveFrom = now.AddDays(-10),
            ImportedAt = now.AddDays(-10)
        });
        await context.SaveChangesAsync();

        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            [
                new IndexConstituentEntry("AAPL", "AAPL", "Apple", StockExchanges.Nasdaq, "US0378331005", null, "Information Technology"),
                new IndexConstituentEntry("BAD", "BAD", "Broken Row", null, "US0378331005", null, "Information Technology")
            ]));

        var controller = CreateController(context, provider: provider);
        var refresh = await controller.RefreshConstituents(5910);
        var ok = Assert.IsType<OkObjectResult>(refresh.Result);
        var payload = Assert.IsType<IndexConstituentsRefreshResponse>(ok.Value);
        Assert.Equal("Partial", payload.ProviderStatus);
        Assert.Equal(0, payload.Closed);

        var existingMembership = await context.StockMarketIndices
            .SingleAsync(x => x.StockId == 59101 && x.MarketIndexId == 5910);
        Assert.Null(existingMembership.EffectiveTo);
    }

    [Fact]
    public async Task GetConstituents_ProjectsSectorAndIndustryFields()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        var sector = new Sector
        {
            Id = 501,
            Name = "Information Technology",
            NormalizedName = "INFORMATION TECHNOLOGY",
            IsArchived = false,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var industry = new Industry
        {
            Id = 601,
            Name = "Software",
            NormalizedName = "SOFTWARE",
            SectorId = sector.Id,
            Sector = sector,
            IsArchived = false,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var stock = new Stock
        {
            Id = 5911,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nasdaq,
            Sector = sector,
            Industry = industry,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        };
        context.Sectors.Add(sector);
        context.Industries.Add(industry);
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = stock.Id,
            MarketIndexId = 1,
            Source = "Test",
            EffectiveFrom = now,
            ImportedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituents(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<IndexConstituentsResponse>(ok.Value);
        var dto = Assert.Single(payload.Constituents, x => x.StockId == 5911);

        Assert.Equal(501, dto.SectorId);
        Assert.Equal("Information Technology", dto.Sector);
        Assert.Equal(601, dto.IndustryId);
        Assert.Equal("Software", dto.Industry);
    }

    [Fact]
    public async Task GetConstituents_UsesStockSectorIdFallbackWhenIndustryMissing()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;

        var sector = new Sector
        {
            Id = 511,
            Name = "Energy",
            NormalizedName = "ENERGY",
            IsArchived = false,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var stock = new Stock
        {
            Id = 5912,
            Ticker = "XOM",
            Name = "Exxon Mobil",
            CommonName = "Exxon Mobil",
            Exchange = StockExchanges.Nyse,
            SectorId = sector.Id,
            Sector = sector,
            IndustryId = null,
            Industry = null,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        };
        context.Sectors.Add(sector);
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = stock.Id,
            MarketIndexId = 1,
            Source = "Test",
            EffectiveFrom = now,
            ImportedAt = now
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituents(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<IndexConstituentsResponse>(ok.Value);
        var dto = Assert.Single(payload.Constituents, x => x.StockId == stock.Id);

        Assert.Equal(sector.Id, dto.SectorId);
        Assert.Equal("Energy", dto.Sector);
        Assert.Null(dto.IndustryId);
        Assert.Null(dto.Industry);
    }

    [Fact]
    public async Task RefreshConstituents_RepeatedRun_IsIdempotentForStocksMembershipsAndSectors()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5912,
            Name = "Idempotency Test",
            NormalizedName = "IDEMPOTENCY TEST",
            Code = "IDP5912",
            NormalizedCode = "IDP5912",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();

        IndexConstituentEntry[] entries =
        [
            new IndexConstituentEntry("AAPL", "AAPL", "Apple Inc.", StockExchanges.Nasdaq, "US0378331005", "865985", "Information Technology"),
            new IndexConstituentEntry("MSFT", "MSFT", "Microsoft Corporation", StockExchanges.Nasdaq, "US5949181045", "870747", "Information Technology"),
        ];
        var provider = new StaticIndexConstituentsProvider(new IndexConstituentsResult(
            IndexConstituentsStatus.Success,
            "TestProvider",
            now,
            entries));

        var controller = CreateController(context, provider: provider);
        Assert.IsType<OkObjectResult>((await controller.RefreshConstituents(5912)).Result);
        Assert.IsType<OkObjectResult>((await controller.RefreshConstituents(5912)).Result);

        Assert.Equal(2, await context.Stocks.CountAsync());
        Assert.Equal(2, await context.StockMarketIndices.CountAsync());
        Assert.Equal(1, await context.Sectors.CountAsync());
        Assert.Equal(2, await context.StockMarketIndices.CountAsync(x => x.MarketIndexId == 5912 && x.EffectiveTo == null));
    }

    [Fact]
    public async Task GetConstituentHistory_CurrentTrackedMember_ReturnsHistory()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 5902,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 5902, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var service = new TestStockHistoryReadService();
        var controller = CreateController(context, stockHistoryService: service);
        var result = await controller.GetConstituentHistory(1, 5902, "24h");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StockHistoryResponse>(ok.Value);
        Assert.Equal("24h", payload.Range);
        Assert.Equal((5902, "24h"), Assert.Single(service.GetHistoryCalls));
    }

    [Fact]
    public async Task GetConstituentHistory_MissingIndexMissingOrFormerMember_ReturnsNotFound()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 5903,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = 5903,
            MarketIndexId = 1,
            EffectiveFrom = now.AddDays(-5),
            EffectiveTo = now.AddDays(-1)
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context, stockHistoryService: new TestStockHistoryReadService());
        var missingIndex = await controller.GetConstituentHistory(999999, 5903, "1y");
        var formerMember = await controller.GetConstituentHistory(1, 5903, "1y");
        var missingStock = await controller.GetConstituentHistory(1, 999998, "1y");

        Assert.IsType<NotFoundObjectResult>(missingIndex.Result);
        Assert.IsType<NotFoundObjectResult>(formerMember.Result);
        Assert.IsType<NotFoundObjectResult>(missingStock.Result);
    }

    [Fact]
    public async Task GetConstituentHistory_InvalidRangeOrInvalidStockData_ReturnsBadRequest()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 5904,
            Ticker = "   ",
            Name = "Bad",
            CommonName = "Bad",
            Exchange = "???",
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 5904, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var controller = CreateController(context, stockHistoryService: new TestStockHistoryReadService());
        var invalidRange = await controller.GetConstituentHistory(1, 5904, "bad-range");
        var invalidStock = await controller.GetConstituentHistory(1, 5904, "1y");

        Assert.IsType<BadRequestObjectResult>(invalidRange.Result);
        Assert.IsType<BadRequestObjectResult>(invalidStock.Result);
    }

    [Fact]
    public async Task GetConstituentHistory_ArchivedIndex_AllowsReadForCurrentMember()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 5905,
            Name = "Archived 5905",
            NormalizedName = "ARCHIVED 5905",
            Code = "AR5905",
            NormalizedCode = "AR5905",
            IsArchived = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.Add(new Stock
        {
            Id = 59051,
            Ticker = "IBM",
            Name = "IBM",
            CommonName = "IBM",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 59051, MarketIndexId = 5905, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var service = new TestStockHistoryReadService();
        var controller = CreateController(context, stockHistoryService: service);
        var result = await controller.GetConstituentHistory(5905, 59051, "1m");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<StockHistoryResponse>(ok.Value);
        Assert.Equal((59051, "1m"), Assert.Single(service.GetHistoryCalls));
    }

    [Fact]
    public async Task RefreshConstituentHistory_CurrentCatalogOnlyMember_ReturnsAcceptedJobWithoutTrackingMutation()
    {
        await using var context = await CreateSqliteContextAsync();
        var stock = new Stock
        {
            Id = 6001,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = stock.Id, MarketIndexId = 1, EffectiveFrom = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var jobs = new TestIndexConstituentHistoryRefreshJobService();
        var controller = CreateController(context, constituentHistoryJobService: jobs);
        var result = await controller.RefreshConstituentHistory(1, stock.Id);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var payload = Assert.IsType<IndexConstituentHistoryRefreshJobResponse>(accepted.Value);
        Assert.Equal(stock.Id, payload.StockId);
        Assert.Equal(1, payload.MarketIndexId);
        Assert.Equal(IndexConstituentHistoryRefreshJobState.Queued, payload.State);
        Assert.Single(jobs.EnqueueCalls);
        Assert.Equal((1, stock.Id), jobs.EnqueueCalls[0]);

        var persisted = await context.Stocks.AsNoTracking().SingleAsync(x => x.Id == stock.Id);
        Assert.Equal(StockTrackingStatus.CatalogOnly, persisted.TrackingStatus);
    }

    [Fact]
    public async Task RefreshConstituentHistory_TrackedMember_ReturnsAccepted()
    {
        await using var context = await CreateSqliteContextAsync();
        var stock = new Stock
        {
            Id = 6002,
            Ticker = "MSFT",
            Name = "Microsoft",
            CommonName = "Microsoft",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.Tracked,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = stock.Id, MarketIndexId = 1, EffectiveFrom = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = CreateController(context, constituentHistoryJobService: new TestIndexConstituentHistoryRefreshJobService());
        var result = await controller.RefreshConstituentHistory(1, stock.Id);

        var ok = Assert.IsType<AcceptedResult>(result.Result);
        var payload = Assert.IsType<IndexConstituentHistoryRefreshJobResponse>(ok.Value);
        Assert.Equal(stock.Id, payload.StockId);
    }

    [Fact]
    public async Task RefreshConstituentHistory_MissingOrFormerMember_ReturnsNotFound()
    {
        await using var context = await CreateSqliteContextAsync();
        var stock = new Stock
        {
            Id = 6003,
            Ticker = "SAP",
            Name = "SAP",
            CommonName = "SAP",
            Exchange = StockExchanges.Frankfurt,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = DateTime.UtcNow
        };
        context.Stocks.Add(stock);
        context.StockMarketIndices.Add(new StockMarketIndex
        {
            StockId = stock.Id,
            MarketIndexId = 1,
            EffectiveFrom = DateTime.UtcNow.AddDays(-10),
            EffectiveTo = DateTime.UtcNow.AddDays(-1)
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var missingIndex = await controller.RefreshConstituentHistory(999999, stock.Id);
        var formerMember = await controller.RefreshConstituentHistory(1, stock.Id);
        var missingStock = await controller.RefreshConstituentHistory(1, 999998);

        Assert.IsType<NotFoundObjectResult>(missingIndex.Result);
        Assert.IsType<NotFoundObjectResult>(formerMember.Result);
        Assert.IsType<NotFoundObjectResult>(missingStock.Result);
    }

    [Fact]
    public async Task RefreshConstituentHistory_ArchivedIndexOrInvalidStockData_ReturnsError()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.MarketIndices.Add(new MarketIndex
        {
            Id = 6004,
            Name = "Archived",
            NormalizedName = "ARCHIVED",
            Code = "AR6004",
            NormalizedCode = "AR6004",
            IsArchived = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.Stocks.AddRange(
            new Stock
            {
                Id = 60041,
                Ticker = "IBM",
                Name = "IBM",
                CommonName = "IBM",
                Exchange = StockExchanges.Nyse,
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now
            },
            new Stock
            {
                Id = 60042,
                Ticker = "   ",
                Name = "BadTicker",
                CommonName = "BadTicker",
                Exchange = "???",
                TrackingStatus = StockTrackingStatus.CatalogOnly,
                UpdatedAt = now
            });
        context.StockMarketIndices.AddRange(
            new StockMarketIndex { StockId = 60041, MarketIndexId = 6004, EffectiveFrom = now },
            new StockMarketIndex { StockId = 60042, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var archived = await controller.RefreshConstituentHistory(6004, 60041);
        var invalid = await controller.RefreshConstituentHistory(1, 60042);

        Assert.IsType<ConflictObjectResult>(archived.Result);
        Assert.IsType<BadRequestObjectResult>(invalid.Result);
    }

    [Fact]
    public async Task RefreshConstituentHistory_DuplicateStarts_ReusesActiveJob()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 60043,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 60043, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var jobs = new TestIndexConstituentHistoryRefreshJobService(reuseSameActiveJob: true);
        var controller = CreateController(context, constituentHistoryJobService: jobs);

        var first = await controller.RefreshConstituentHistory(1, 60043);
        var second = await controller.RefreshConstituentHistory(1, 60043);

        var firstAccepted = Assert.IsType<AcceptedResult>(first.Result);
        var secondAccepted = Assert.IsType<AcceptedResult>(second.Result);
        var firstPayload = Assert.IsType<IndexConstituentHistoryRefreshJobResponse>(firstAccepted.Value);
        var secondPayload = Assert.IsType<IndexConstituentHistoryRefreshJobResponse>(secondAccepted.Value);
        Assert.Equal(firstPayload.JobId, secondPayload.JobId);
        Assert.False(firstPayload.ReusedActiveJob);
        Assert.True(secondPayload.ReusedActiveJob);
    }

    [Fact]
    public async Task GetConstituentHistoryRefreshJobStatus_MustMatchIndexStockJobAndUnknownReturns404()
    {
        await using var context = await CreateSqliteContextAsync();
        var jobs = new TestIndexConstituentHistoryRefreshJobService();
        var controller = CreateController(context, constituentHistoryJobService: jobs);

        var started = jobs.Enqueue(1, 123);
        Assert.NotNull(started.Job);
        var job = started.Job!;

        var ok = controller.GetConstituentHistoryRefreshJobStatus(1, 123, job.JobId);
        var wrongIndex = controller.GetConstituentHistoryRefreshJobStatus(2, 123, job.JobId);
        var wrongStock = controller.GetConstituentHistoryRefreshJobStatus(1, 124, job.JobId);
        var unknown = controller.GetConstituentHistoryRefreshJobStatus(1, 123, "missing");

        Assert.IsType<OkObjectResult>(ok.Result);
        Assert.IsType<NotFoundObjectResult>(wrongIndex.Result);
        Assert.IsType<NotFoundObjectResult>(wrongStock.Result);
        Assert.IsType<NotFoundObjectResult>(unknown.Result);
    }

    [Fact]
    public async Task RefreshConstituentsHistory_BatchIsSequential_ContinuesAfterFailures_AndStopsOnRateLimit()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.AddRange(
            new Stock { Id = 6010, Ticker = "BBB", Name = "B", CommonName = "B", Exchange = StockExchanges.Nyse, TrackingStatus = StockTrackingStatus.CatalogOnly, UpdatedAt = now },
            new Stock { Id = 6009, Ticker = "AAA", Name = "A", CommonName = "A", Exchange = StockExchanges.Nyse, TrackingStatus = StockTrackingStatus.CatalogOnly, UpdatedAt = now },
            new Stock { Id = 6011, Ticker = "CCC", Name = "C", CommonName = "C", Exchange = StockExchanges.Nyse, TrackingStatus = StockTrackingStatus.CatalogOnly, UpdatedAt = now },
            new Stock { Id = 6012, Ticker = "DDD", Name = "D", CommonName = "D", Exchange = StockExchanges.Nyse, TrackingStatus = StockTrackingStatus.CatalogOnly, UpdatedAt = now });
        context.StockMarketIndices.AddRange(
            new StockMarketIndex { StockId = 6010, MarketIndexId = 1, EffectiveFrom = now },
            new StockMarketIndex { StockId = 6009, MarketIndexId = 1, EffectiveFrom = now },
            new StockMarketIndex { StockId = 6011, MarketIndexId = 1, EffectiveFrom = now },
            new StockMarketIndex { StockId = 6012, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var service = new TestStockHistoryService(async (stock, _) =>
        {
            await Task.Delay(5);
            return stock.Id switch
            {
                6010 => throw new InvalidOperationException("bad stock data"),
                6011 => new StockHistoryRefreshResponse { StockId = stock.Id, RateLimited = true },
                _ => new StockHistoryRefreshResponse { StockId = stock.Id, DeletedPoints = 1, ImportedPoints = 2 }
            };
        });
        var controller = CreateController(context, stockHistoryService: service);

        var result = await controller.RefreshConstituentsHistory(1);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<IndexConstituentHistoryRefreshBatchResponse>(ok.Value);

        Assert.Equal(new[] { 6009, 6010, 6011 }, service.CallOrder);
        Assert.Equal(1, service.MaxConcurrentCalls);
        Assert.Equal(4, payload.Total);
        Assert.Equal(3, payload.Attempted);
        Assert.Equal(1, payload.Succeeded);
        Assert.Equal(1, payload.Failed);
        Assert.Equal(1, payload.RateLimited);
        Assert.Equal(1, payload.SkippedRateLimited);
        Assert.True(payload.StoppedDueToRateLimit);
        Assert.Contains(payload.Results, x => x.StockId == 6012 && x.Status == "SkippedRateLimited");
    }

    [Fact]
    public async Task RefreshConstituentsHistory_RejectsDuplicateConcurrentBatch()
    {
        await using var context = await CreateSqliteContextAsync();
        var now = DateTime.UtcNow;
        context.Stocks.Add(new Stock
        {
            Id = 6020,
            Ticker = "AAPL",
            Name = "Apple",
            CommonName = "Apple",
            Exchange = StockExchanges.Nyse,
            TrackingStatus = StockTrackingStatus.CatalogOnly,
            UpdatedAt = now
        });
        context.StockMarketIndices.Add(new StockMarketIndex { StockId = 6020, MarketIndexId = 1, EffectiveFrom = now });
        await context.SaveChangesAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestStockHistoryService(async (stock, _) =>
        {
            entered.TrySetResult();
            await gate.Task;
            return new StockHistoryRefreshResponse { StockId = stock.Id };
        });

        var firstController = CreateController(context, stockHistoryService: service);
        var secondController = CreateController(context, stockHistoryService: service);

        var firstBatchTask = firstController.RefreshConstituentsHistory(1);
        await entered.Task;
        var secondBatch = await secondController.RefreshConstituentsHistory(1);

        var conflict = Assert.IsType<ConflictObjectResult>(secondBatch.Result);
        Assert.Equal("Обновление исторических данных для этого индекса уже выполняется.", conflict.Value);

        gate.TrySetResult();
        await firstBatchTask;
    }

    [Fact]
    public async Task Create_DefaultsShowInNavigationToTrue_WhenOmitted()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var result = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Test Nav Default",
            Code = "TNDX",
            SortOrder = 1
        });

        var created = Assert.IsType<ObjectResult>(result.Result);
        var dto = Assert.IsType<MarketIndexDto>(created.Value);
        Assert.True(dto.ShowInNavigation);
    }

    [Fact]
    public async Task Create_CanExplicitlySetShowInNavigationFalse()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var result = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Hidden Index",
            Code = "HIDX",
            SortOrder = 1,
            ShowInNavigation = false
        });

        var created = Assert.IsType<ObjectResult>(result.Result);
        var dto = Assert.IsType<MarketIndexDto>(created.Value);
        Assert.False(dto.ShowInNavigation);
    }

    [Fact]
    public async Task Update_TogglesShowInNavigation_TrueToFalse()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var createResult = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Visible Index",
            Code = "VIDX",
            SortOrder = 1,
            ShowInNavigation = true
        });
        var dto = Assert.IsType<MarketIndexDto>(Assert.IsType<ObjectResult>(createResult.Result).Value);
        Assert.True(dto.ShowInNavigation);

        var updateResult = await controller.UpdateMarketIndex(dto.Id, new UpsertMarketIndexRequest
        {
            Name = dto.Name,
            Code = dto.Code,
            SortOrder = dto.SortOrder,
            ShowInNavigation = false
        });

        var updated = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<MarketIndexDto>(updated.Value);
        Assert.False(updatedDto.ShowInNavigation);
        Assert.Equal("VIDX", updatedDto.Code);
    }

    [Fact]
    public async Task Update_TogglesShowInNavigation_FalseToTrue()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var createResult = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Hidden Index",
            Code = "HIDX2",
            SortOrder = 1,
            ShowInNavigation = false
        });
        var dto = Assert.IsType<MarketIndexDto>(Assert.IsType<ObjectResult>(createResult.Result).Value);

        var updateResult = await controller.UpdateMarketIndex(dto.Id, new UpsertMarketIndexRequest
        {
            Name = dto.Name,
            Code = dto.Code,
            SortOrder = dto.SortOrder,
            ShowInNavigation = true
        });

        var updated = Assert.IsType<OkObjectResult>(updateResult.Result);
        Assert.True(Assert.IsType<MarketIndexDto>(updated.Value).ShowInNavigation);
    }

    [Fact]
    public async Task GetAll_DtoExposesShowInNavigation()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        await controller.CreateMarketIndex(new UpsertMarketIndexRequest { Name = "Vis", Code = "VIS1", SortOrder = 1, ShowInNavigation = true });
        await controller.CreateMarketIndex(new UpsertMarketIndexRequest { Name = "Hid", Code = "HID1", SortOrder = 2, ShowInNavigation = false });

        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<MarketIndexDto>>(ok.Value).ToList();

        Assert.Contains(items, x => x.Code == "VIS1" && x.ShowInNavigation == true);
        Assert.Contains(items, x => x.Code == "HID1" && x.ShowInNavigation == false);
    }

    [Fact]
    public async Task Update_ShowInNavigation_DoesNotAffectArchiveStatus()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var createResult = await controller.CreateMarketIndex(new UpsertMarketIndexRequest
        {
            Name = "Archive Check",
            Code = "ARCHCK",
            SortOrder = 1,
            ShowInNavigation = true
        });
        var dto = Assert.IsType<MarketIndexDto>(Assert.IsType<ObjectResult>(createResult.Result).Value);

        await controller.ArchiveMarketIndex(dto.Id);

        var updateResult = await controller.UpdateMarketIndex(dto.Id, new UpsertMarketIndexRequest
        {
            Name = dto.Name,
            Code = dto.Code,
            SortOrder = dto.SortOrder,
            ShowInNavigation = false
        });

        var updated = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updatedDto = Assert.IsType<MarketIndexDto>(updated.Value);
        Assert.True(updatedDto.IsArchived, "Archive status must be preserved after visibility update");
        Assert.False(updatedDto.ShowInNavigation);
    }

    // ── GetConstituentPerformance tests ────────────────────────────────────────

    [Fact]
    public async Task GetConstituentPerformance_InvalidRange_Returns400()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);
        var marketIndex = await context.MarketIndices.FirstAsync();

        var result = await controller.GetConstituentPerformance(marketIndex.Id, range: "bad-range");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetConstituentPerformance_MissingIndex_Returns404()
    {
        await using var context = await CreateSqliteContextAsync();
        var controller = CreateController(context);

        var result = await controller.GetConstituentPerformance(99999);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetConstituentPerformance_EmptyIndex_ReturnsEmptyItems()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = await context.MarketIndices.FirstAsync();
        var controller = CreateController(context);

        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Equal(marketIndex.Id, dto.MarketIndexId);
        Assert.Equal("1y", dto.Range);
        Assert.Empty(dto.Items);
    }

    [Fact]
    public async Task GetConstituentPerformance_NormalizesRange()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = await context.MarketIndices.FirstAsync();
        var controller = CreateController(context);

        var result = await controller.GetConstituentPerformance(marketIndex.Id, "  1Y  ");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Equal("1y", dto.Range);
    }

    [Fact]
    public async Task GetConstituentPerformance_24h_WithSparseIntradayHistory_ReturnsInsufficientData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDX24A", SortOrder = 980 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock
        {
            Ticker = "SNAP24",
            Exchange = "NYSE",
            Name = "Snapshot 24h",
            TrackingStatus = StockTrackingStatus.Tracked,
            CurrentPrice = 105m,
            CurrentPriceChange = 5m,
            CurrentPriceChangePercent = 5m,
            CurrentPriceAt = DateTime.UtcNow,
        };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex
        {
            MarketIndexId = marketIndex.Id,
            StockId = stock.Id,
            EffectiveTo = null,
        });

        // One intraday point is insufficient for the history-only path.
        context.StockHistoricalPrices.Add(new StockHistoricalPrice
        {
            StockId = stock.Id,
            Timestamp = DateTime.UtcNow.AddHours(-2),
            Interval = "10m",
            Close = 104m,
            QuoteUnitMultiplier = 1m,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "24h");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        var item = Assert.Single(dto.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public async Task GetConstituentPerformance_24h_WithoutHistoryAndSnapshot_ReturnsInsufficientData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDX24B", SortOrder = 981 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "NOSNAP", Exchange = "NYSE", Name = "No snapshot", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex
        {
            MarketIndexId = marketIndex.Id,
            StockId = stock.Id,
            EffectiveTo = null,
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "24h");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public async Task GetConstituentPerformance_FormerMembersExcluded()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP1", SortOrder = 990 };
        context.MarketIndices.Add(marketIndex);
        var currentStock = new Stock { Ticker = "CURR", Exchange = "NYSE", Name = "Current", TrackingStatus = StockTrackingStatus.Tracked };
        var formerStock = new Stock { Ticker = "FMRS", Exchange = "NYSE", Name = "Former", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.AddRange(currentStock, formerStock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = currentStock.Id, EffectiveTo = null });
        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = formerStock.Id, EffectiveTo = DateTime.UtcNow.AddDays(-30) });

        // Add history for both stocks
        var now = DateTime.UtcNow;
        var interval = "1wk";
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = currentStock.Id, Timestamp = now.AddDays(-14), Interval = interval, Close = 100m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = currentStock.Id, Timestamp = now.AddDays(-7), Interval = interval, Close = 110m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = formerStock.Id, Timestamp = now.AddDays(-14), Interval = interval, Close = 50m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = formerStock.Id, Timestamp = now.AddDays(-7), Interval = interval, Close = 60m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Single(dto.Items);
        Assert.Equal(currentStock.Id, dto.Items[0].StockId);
    }

    [Fact]
    public async Task GetConstituentPerformance_PositiveChange_ComputedCorrectly()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP2", SortOrder = 991 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "GROW", Exchange = "NYSE", Name = "GrowCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 125m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        var item = Assert.Single(dto.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        Assert.NotNull(item.ChangePercent);
        Assert.Equal(25.0, item.ChangePercent!.Value, precision: 6);
    }

    [Fact]
    public async Task GetConstituentPerformance_NegativeChange_ComputedCorrectly()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP3", SortOrder = 992 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "DROP", Exchange = "NYSE", Name = "DropCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 200m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 160m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        var item = Assert.Single(dto.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        Assert.NotNull(item.ChangePercent);
        Assert.Equal(-20.0, item.ChangePercent!.Value, precision: 6);
    }

    [Fact]
    public async Task GetConstituentPerformance_ZeroChange_ComputedCorrectly()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP4", SortOrder = 993 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "FLAT", Exchange = "NYSE", Name = "FlatCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        Assert.Equal(0.0, item.ChangePercent!.Value, precision: 6);
    }

    [Fact]
    public async Task GetConstituentPerformance_StartPriceZero_ReturnsInsufficientData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP5", SortOrder = 994 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "ZERO", Exchange = "NYSE", Name = "ZeroCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 0m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 10m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public async Task GetConstituentPerformance_FewerThanTwoPoints_ReturnsInsufficientData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP6", SortOrder = 995 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "SPARSE", Exchange = "NYSE", Name = "SparseCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        // Only one point
        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.Add(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
        Assert.Null(item.ChangePercent);
    }

    [Fact]
    public async Task GetConstituentPerformance_NoHistoryPoints_ReturnsInsufficientData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP7", SortOrder = 996 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "NODATA", Exchange = "NYSE", Name = "NoCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, item.DataStatus);
    }

    [Fact]
    public async Task GetConstituentPerformance_OneStockMissingData_DoesNotFailOthers()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP8", SortOrder = 997 };
        context.MarketIndices.Add(marketIndex);
        var stockA = new Stock { Ticker = "AOKAY", Exchange = "NYSE", Name = "AОК", TrackingStatus = StockTrackingStatus.Tracked };
        var stockB = new Stock { Ticker = "BNODAT", Exchange = "NYSE", Name = "BNone", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.AddRange(stockA, stockB);
        await context.SaveChangesAsync();

        context.StockMarketIndices.AddRange(
            new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stockA.Id, EffectiveTo = null },
            new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stockB.Id, EffectiveTo = null }
        );

        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stockA.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stockA.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 110m, QuoteUnitMultiplier = 1m }
            // stockB has no history
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Equal(2, dto.Items.Count);

        var a = dto.Items.Single(x => x.StockId == stockA.Id);
        var b = dto.Items.Single(x => x.StockId == stockB.Id);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, a.DataStatus);
        Assert.Equal(ConstituentPerformanceDataStatus.InsufficientData, b.DataStatus);
        Assert.Null(b.ChangePercent);
    }

    [Fact]
    public async Task GetConstituentPerformance_UsesChronologicalFirstAndLastPoints()
    {
        // Verifies earliest/latest points chosen correctly even if inserted out of order.
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXP9", SortOrder = 998 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "ORDER", Exchange = "NYSE", Name = "OrderCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        // Insert out of chronological order
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 130m, QuoteUnitMultiplier = 1m },  // latest
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-21), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m }, // earliest
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 115m, QuoteUnitMultiplier = 1m }  // middle
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        // start=100, end=130 → 30%
        Assert.Equal(30.0, item.ChangePercent!.Value, precision: 6);
        Assert.Equal(100m, item.StartPrice);
        Assert.Equal(130m, item.EndPrice);
    }

    [Fact]
    public async Task GetConstituentPerformance_QuoteUnitMultiplierApplied()
    {
        // GBp stocks have QuoteUnitMultiplier=0.01; percentage should still be correct
        // because the multiplier is constant across both endpoints and cancels out.
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "TestIdx", Code = "TIDXPA", SortOrder = 999 };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "GBP", Exchange = "Frankfurt", Name = "GbpCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });

        var now = DateTime.UtcNow;
        // Raw prices in pence; multiplier converts to GBP
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 10000m, QuoteUnitMultiplier = 0.01m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 11000m, QuoteUnitMultiplier = 0.01m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var item = Assert.Single(Assert.IsType<IndexConstituentPerformanceResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value).Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, item.DataStatus);
        // 100 GBP → 110 GBP → +10%
        Assert.Equal(10.0, item.ChangePercent!.Value, precision: 6);
        Assert.Equal(100m, item.StartPrice);  // 10000 × 0.01
        Assert.Equal(110m, item.EndPrice);    // 11000 × 0.01
    }

    [Fact]
    public async Task GetConstituentPerformance_ArchivedIndex_ReturnsData()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = new MarketIndex { Name = "Archived", Code = "ARCPF", SortOrder = 9999, IsArchived = true };
        context.MarketIndices.Add(marketIndex);
        var stock = new Stock { Ticker = "ARCH", Exchange = "NYSE", Name = "ArchCo", TrackingStatus = StockTrackingStatus.Tracked };
        context.Stocks.Add(stock);
        await context.SaveChangesAsync();

        context.StockMarketIndices.Add(new StockMarketIndex { MarketIndexId = marketIndex.Id, StockId = stock.Id, EffectiveTo = null });
        var now = DateTime.UtcNow;
        context.StockHistoricalPrices.AddRange(
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-14), Interval = "1wk", Close = 100m, QuoteUnitMultiplier = 1m },
            new StockHistoricalPrice { StockId = stock.Id, Timestamp = now.AddDays(-7), Interval = "1wk", Close = 105m, QuoteUnitMultiplier = 1m }
        );
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetConstituentPerformance(marketIndex.Id, "1y");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Single(dto.Items);
        Assert.Equal(ConstituentPerformanceDataStatus.Available, dto.Items[0].DataStatus);
    }

    [Fact]
    public async Task GetConstituentPerformance_ResponseIncludesCorrectMarketIndexIdAndRange()
    {
        await using var context = await CreateSqliteContextAsync();
        var marketIndex = await context.MarketIndices.FirstAsync();
        var controller = CreateController(context);

        var result = await controller.GetConstituentPerformance(marketIndex.Id, "3m");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<IndexConstituentPerformanceResponse>(ok.Value);
        Assert.Equal(marketIndex.Id, dto.MarketIndexId);
        Assert.Equal("3m", dto.Range);
        Assert.True(dto.GeneratedAtUtc <= DateTime.UtcNow);
        Assert.True(dto.GeneratedAtUtc > DateTime.UtcNow.AddSeconds(-10));
    }

    private static async Task<AppDbContext> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static MarketIndicesController CreateController(
        AppDbContext context,
        IIndexConstituentsProvider? provider = null,
        IStockHistoryService? stockHistoryService = null,
        IIndexConstituentHistoryRefreshJobService? constituentHistoryJobService = null)
    {
        return new MarketIndicesController(
            context,
            new NullMarketIndexHistoryService(),
            provider ?? new NullIndexConstituentsProvider(),
            stockHistoryService ?? new NullStockHistoryService(),
            new StockPerformanceCalculationService(context, TimeProvider.System),
            constituentHistoryJobService ?? new NullIndexConstituentHistoryRefreshJobService(),
            new NullIndexConstituentsBatchQuoteRefreshJobService(),
            NullLogger<MarketIndicesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
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

    private sealed class StaticIndexConstituentsProvider(IndexConstituentsResult result) : IIndexConstituentsProvider
    {
        public string ProviderName => result.ProviderName;

        public Task<IndexConstituentsResult> GetConstituentsAsync(MarketIndex index, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class NullStockHistoryService : IStockHistoryService
    {
        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryResponse());

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryRefreshResponse { StockId = stock.Id });
        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);
    }

    private sealed class TestStockHistoryService(
        Func<Stock, CancellationToken, Task<StockHistoryRefreshResponse>> refreshHandler) : IStockHistoryService
    {
        private int _activeCalls;
        private int _maxConcurrentCalls;
        private readonly List<int> _callOrder = [];
        private readonly object _sync = new();

        public IReadOnlyList<int> CallOrder
        {
            get
            {
                lock (_sync)
                {
                    return _callOrder.ToList();
                }
            }
        }

        public int MaxConcurrentCalls => _maxConcurrentCalls;

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryResponse());

        public async Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _callOrder.Add(stock.Id);
            }

            var current = Interlocked.Increment(ref _activeCalls);
            var max = Volatile.Read(ref _maxConcurrentCalls);
            while (current > max)
            {
                var updated = Interlocked.CompareExchange(ref _maxConcurrentCalls, current, max);
                if (updated == max)
                {
                    break;
                }

                max = updated;
            }

            try
            {
                return await refreshHandler(stock, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);
    }

    private sealed class TestStockHistoryReadService : IStockHistoryService
    {
        private readonly List<(int StockId, string Range)> _getHistoryCalls = [];

        public IReadOnlyList<(int StockId, string Range)> GetHistoryCalls => _getHistoryCalls;

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
        {
            _getHistoryCalls.Add((stock.Id, range));
            return Task.FromResult(new StockHistoryResponse
            {
                Range = range,
                Interval = "1d"
            });
        }

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryRefreshResponse { StockId = stock.Id });
        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);
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

    private sealed class TestIndexConstituentHistoryRefreshJobService(bool reuseSameActiveJob = false)
        : IIndexConstituentHistoryRefreshJobService
    {
        private readonly ConcurrentDictionary<string, IndexConstituentHistoryRefreshJobResponse> _jobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, string> _activeByStock = new();

        public List<(int IndexId, int StockId)> EnqueueCalls { get; } = [];

        public IndexConstituentHistoryRefreshJobEnqueueResult Enqueue(int marketIndexId, int stockId)
        {
            EnqueueCalls.Add((marketIndexId, stockId));

            if (reuseSameActiveJob
                && _activeByStock.TryGetValue(stockId, out var activeJobId)
                && _jobs.TryGetValue(activeJobId, out var existing))
            {
                return new IndexConstituentHistoryRefreshJobEnqueueResult
                {
                    Status = IndexConstituentHistoryRefreshJobEnqueueStatus.ReusedActiveJob,
                    Job = new IndexConstituentHistoryRefreshJobResponse
                    {
                        JobId = existing.JobId,
                        MarketIndexId = existing.MarketIndexId,
                        StockId = existing.StockId,
                        State = existing.State,
                        ReusedActiveJob = true,
                        CreatedAtUtc = existing.CreatedAtUtc,
                        StartedAtUtc = existing.StartedAtUtc,
                        CompletedAtUtc = existing.CompletedAtUtc,
                        ExpiresAtUtc = existing.ExpiresAtUtc,
                        DeletedPoints = existing.DeletedPoints,
                        ImportedPoints = existing.ImportedPoints,
                        Error = existing.Error
                    }
                };
            }

            var job = new IndexConstituentHistoryRefreshJobResponse
            {
                JobId = Guid.NewGuid().ToString("N"),
                MarketIndexId = marketIndexId,
                StockId = stockId,
                State = IndexConstituentHistoryRefreshJobState.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };
            _jobs[job.JobId] = job;
            _activeByStock[stockId] = job.JobId;
            return new IndexConstituentHistoryRefreshJobEnqueueResult
            {
                Status = IndexConstituentHistoryRefreshJobEnqueueStatus.Enqueued,
                Job = job
            };
        }

        public bool TryGetJob(int marketIndexId, int stockId, string jobId, out IndexConstituentHistoryRefreshJobResponse? job)
        {
            if (!_jobs.TryGetValue(jobId, out var existing)
                || existing.MarketIndexId != marketIndexId
                || existing.StockId != stockId)
            {
                job = null;
                return false;
            }

            job = existing;
            return true;
        }
    }
}
