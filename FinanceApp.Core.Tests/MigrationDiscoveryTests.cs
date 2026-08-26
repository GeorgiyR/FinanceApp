using System.Reflection;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FinanceApp.Core.Tests;

public class MigrationDiscoveryTests
{
    [Fact]
    public void GetMigrations_Contains_AddStockTrackingAndMembershipHistory()
    {
        using var context = CreateContext();

        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260816162000_AddStockTrackingAndMembershipHistory", migrations);

        var migrationType = typeof(AppDbContext).Assembly.GetType("FinanceApp.Data.Migrations.AddStockTrackingAndMembershipHistory");
        Assert.NotNull(migrationType);

        var dbContextAttribute = migrationType!.GetCustomAttribute<DbContextAttribute>();
        Assert.NotNull(dbContextAttribute);
        Assert.Equal(typeof(AppDbContext), dbContextAttribute!.ContextType);

        var migrationAttribute = migrationType.GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(migrationAttribute);
        Assert.Equal("20260816162000_AddStockTrackingAndMembershipHistory", migrationAttribute!.Id);
    }

    [Fact]
    public void GetMigrations_Contains_AddAdjustedCloseAndNonUniqueIsin()
    {
        using var context = CreateContext();

        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260820043053_AddAdjustedCloseAndNonUniqueIsin", migrations);

        var migrationType = typeof(AppDbContext).Assembly.GetType("FinanceApp.Data.Migrations.AddAdjustedCloseAndNonUniqueIsin");
        Assert.NotNull(migrationType);

        var migrationAttribute = migrationType!.GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(migrationAttribute);
        Assert.Equal("20260820043053_AddAdjustedCloseAndNonUniqueIsin", migrationAttribute!.Id);
    }

    [Fact]
    public void GetMigrations_Contains_FixCatalogStockRefreshRunOccurrenceIndex()
    {
        using var context = CreateContext();

        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260820072805_FixCatalogStockRefreshRunOccurrenceIndex", migrations);

        var migrationType = typeof(AppDbContext).Assembly.GetType("FinanceApp.Data.Migrations.FixCatalogStockRefreshRunOccurrenceIndex");
        Assert.NotNull(migrationType);

        var migrationAttribute = migrationType!.GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(migrationAttribute);
        Assert.Equal("20260820072805_FixCatalogStockRefreshRunOccurrenceIndex", migrationAttribute!.Id);
    }

    [Fact]
    public void GetMigrations_Contains_AddStockHistoryRefreshCadenceAndScheduling()
    {
        using var context = CreateContext();

        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260820094806_AddStockHistoryRefreshCadenceAndScheduling", migrations);

        var migrationType = typeof(AppDbContext).Assembly.GetType("FinanceApp.Data.Migrations.AddStockHistoryRefreshCadenceAndScheduling");
        Assert.NotNull(migrationType);

        var migrationAttribute = migrationType!.GetCustomAttribute<MigrationAttribute>();
        Assert.NotNull(migrationAttribute);
        Assert.Equal("20260820094806_AddStockHistoryRefreshCadenceAndScheduling", migrationAttribute!.Id);
    }

    [Fact]
    public void Model_Contains_StockTracking_And_MembershipHistory_Metadata()
    {
        using var context = CreateContext();

        var stockEntity = context.Model.FindEntityType(typeof(Stock));
        Assert.NotNull(stockEntity);

        var trackingStatus = stockEntity!.FindProperty(nameof(Stock.TrackingStatus));
        Assert.NotNull(trackingStatus);
        // ValueGeneratedNever: EF always includes TrackingStatus in INSERT statements,
        // regardless of the value. This prevents the bug where CatalogOnly = 0 (the CLR
        // default for int) was omitted from INSERT and MySQL substituted DEFAULT 1.
        Assert.Equal(ValueGenerated.Never, trackingStatus.ValueGenerated);
        // No explicit HasDefaultValue configured; GetDefaultValue returns the CLR default (0 = CatalogOnly).
        Assert.Equal(StockTrackingStatus.CatalogOnly, (StockTrackingStatus?)trackingStatus.GetDefaultValue());
        var purchaseCandidatePriority = stockEntity.FindProperty(nameof(Stock.PurchaseCandidatePriority));
        Assert.NotNull(purchaseCandidatePriority);
        Assert.Equal(ValueGenerated.Never, purchaseCandidatePriority!.ValueGenerated);
        Assert.Equal(StockPurchaseCandidatePriority.None, (StockPurchaseCandidatePriority?)purchaseCandidatePriority.GetDefaultValue());
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_PurchaseCandidatePriority"
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Stock.PurchaseCandidatePriority) }));

        var providerSymbol = stockEntity.FindProperty(nameof(Stock.ProviderSymbol));
        Assert.NotNull(providerSymbol);
        Assert.Equal(50, providerSymbol.GetMaxLength());
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_ProviderSymbol"
                && index.GetFilter() == "`ProviderSymbol` IS NOT NULL"
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Stock.ProviderSymbol) }));
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_Isin"
                && !index.IsUnique
                && index.GetFilter() == "`Isin` IS NOT NULL"
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Stock.Isin) }));

        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.HistoryRefreshCadence)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.LastIncrementalHistoryRefreshSucceededAtUtc)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.NextIncrementalHistoryRefreshAtUtc)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.LastHistoryReconciliationSucceededAtUtc)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.NextHistoryReconciliationAtUtc)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.LastFullHistoryBackfillSucceededAtUtc)));
        Assert.NotNull(stockEntity.FindProperty(nameof(Stock.NextFullHistoryBackfillAtUtc)));
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_HistoryRefreshCadence_NextIncremental_Id");
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_HistoryRefreshCadence_NextReconciliation_Id");
        Assert.Contains(
            stockEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_Stocks_HistoryRefreshCadence_NextFullBackfill_Id");

        var membershipEntity = context.Model.FindEntityType(typeof(StockMarketIndex));
        Assert.NotNull(membershipEntity);

        var importedAt = membershipEntity!.FindProperty(nameof(StockMarketIndex.ImportedAt));
        Assert.NotNull(importedAt);
        Assert.Equal("UTC_TIMESTAMP(6)", importedAt.GetDefaultValueSql());

        Assert.Equal(100, membershipEntity.FindProperty(nameof(StockMarketIndex.Source))?.GetMaxLength());
        Assert.Equal(100, membershipEntity.FindProperty(nameof(StockMarketIndex.ProviderConstituentKey))?.GetMaxLength());
        Assert.Contains(
            membershipEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_StockMarketIndices_StockId_MarketIndexId"
                && index.Properties.Select(property => property.Name).SequenceEqual(new[]
                {
                    nameof(StockMarketIndex.StockId),
                    nameof(StockMarketIndex.MarketIndexId)
                }));

        var historicalPriceEntity = context.Model.FindEntityType(typeof(StockHistoricalPrice));
        Assert.NotNull(historicalPriceEntity);

        var adjustedClose = historicalPriceEntity!.FindProperty(nameof(StockHistoricalPrice.AdjustedClose));
        Assert.NotNull(adjustedClose);
        Assert.Equal("decimal(18,4)", adjustedClose!.GetColumnType());
    }

    [Fact]
    public void Model_Contains_CatalogRefreshRunIndexSemantics()
    {
        using var context = CreateContext();

        var stockRefreshRunEntity = context.Model.FindEntityType(typeof(CatalogStockRefreshRun));
        Assert.NotNull(stockRefreshRunEntity);

        Assert.Contains(
            stockRefreshRunEntity!.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CatalogStockRefreshRuns_RunKey"
                && index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(CatalogStockRefreshRun.RunKey) }));

        Assert.Contains(
            stockRefreshRunEntity.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CatalogStockRefreshRuns_BusinessDate_TimeZoneId"
                && !index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(CatalogStockRefreshRun.BusinessDate), nameof(CatalogStockRefreshRun.TimeZoneId) }));

        var fundamentalsRefreshRunEntity = context.Model.FindEntityType(typeof(CatalogFundamentalsRefreshRun));
        Assert.NotNull(fundamentalsRefreshRunEntity);

        Assert.Contains(
            fundamentalsRefreshRunEntity!.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CatalogFundamentalsRefreshRuns_BusinessWeek_TimeZoneId"
                && index.IsUnique
                && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(CatalogFundamentalsRefreshRun.BusinessWeek), nameof(CatalogFundamentalsRefreshRun.TimeZoneId) }));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new AppDbContext(options);
    }
}
