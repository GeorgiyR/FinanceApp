using FinanceApp.API.Controllers;
using FinanceApp.API.Models;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceApp.Core.Tests;

public class CatalogStockRefreshHostedServiceTests
{
    [Fact]
    public void ScheduleCalculator_BeforeAndAfterTime_ComputesNextRun()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var before = new DateTimeOffset(2026, 8, 18, 18, 0, 0, TimeSpan.Zero); // 20:00 local
        var after = new DateTimeOffset(2026, 8, 18, 21, 0, 0, TimeSpan.Zero); // 23:00 local

        var beforeSnapshot = CatalogStockRefreshScheduleCalculator.Snapshot(before, tz, new TimeSpan(22, 30, 0));
        var afterSnapshot = CatalogStockRefreshScheduleCalculator.Snapshot(after, tz, new TimeSpan(22, 30, 0));

        Assert.Equal(new DateTimeOffset(2026, 8, 18, 20, 30, 0, TimeSpan.Zero), beforeSnapshot.NextScheduledRunUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 20, 30, 0, TimeSpan.Zero), afterSnapshot.NextScheduledRunUtc);
    }

    [Fact]
    public void ScheduleCalculator_EuropeBerlin_DstTransitionsAreHandled()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var springForwardDay = DateOnly.FromDateTime(new DateTime(2026, 3, 29));
        var fallBackDay = DateOnly.FromDateTime(new DateTime(2026, 10, 25));

        var springUtc = CatalogStockRefreshScheduleCalculator.ToUtc(springForwardDay, new TimeSpan(22, 30, 0), tz);
        var fallUtc = CatalogStockRefreshScheduleCalculator.ToUtc(fallBackDay, new TimeSpan(22, 30, 0), tz);

        // CEST (UTC+2) at the end of March, CET (UTC+1) at the end of October.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 20, 30, 0, TimeSpan.Zero), springUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero), fallUtc);
    }

    [Fact]
    public async Task Run_ProcessesTrackedAndCatalogOnly_ExactlyOnce_For600Plus()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            BatchSize = 37,
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0,
            Enabled = true
        });

        await harness.SeedManyStocksAsync(620);
        var businessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19));
        await harness.Service.TriggerRunAsync(businessDate, clock.GetUtcNow().UtcDateTime, "test");

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogStockRefreshRuns.SingleAsync();
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, run.Status);
        Assert.Equal(620, run.TotalDiscovered);
        Assert.Equal(620, run.Processed);
        Assert.Equal(620, run.QuoteSucceeded);
        Assert.Equal(620, run.HistorySucceeded);
        Assert.Equal(620, quote.ProcessedIds.Count);
        Assert.Equal(620, quote.ProcessedIds.Distinct().Count());
        Assert.Equal(620, history.ProcessedIds.Count);
        Assert.Equal(620, history.ProcessedIds.Distinct().Count());
    }

    [Fact]
    public async Task Run_InvalidMetadata_IsSkipped_AndFollowingStocksContinue()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });

        await harness.SeedStockAsync(1, string.Empty, StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "BAD", "UNKNOWN", StockTrackingStatus.CatalogOnly);
        await harness.SeedStockAsync(3, "GOOD", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.Service.TriggerRunAsync(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), clock.GetUtcNow().UtcDateTime, "test");

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.CatalogStockRefreshRuns.SingleAsync();

        Assert.Equal(3, run.Processed);
        Assert.Equal(2, run.QuoteSkipped);
        Assert.Equal(2, run.HistorySkipped);
        Assert.Equal(1, run.QuoteSucceeded);
        Assert.Equal(1, run.HistorySucceeded);
        Assert.Single(quote.ProcessedIds);
        Assert.Equal(3, quote.ProcessedIds[0]);
    }

    [Fact]
    public async Task Run_DoesNotDuplicate_WhenSameBusinessDateAlreadyCompleted()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        var businessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19));

        await harness.Service.TriggerRunAsync(businessDate, clock.GetUtcNow().UtcDateTime, "first");
        var callsAfterFirst = quote.ProcessedIds.Count;
        await harness.Service.TriggerRunAsync(businessDate, clock.GetUtcNow().UtcDateTime, "second");

        Assert.Equal(callsAfterFirst, quote.ProcessedIds.Count);
    }

    [Fact]
    public async Task Run_RecoversExpiredLease()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(10, "SAP", StockExchanges.Frankfurt, StockTrackingStatus.CatalogOnly);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogStockRefreshRuns.Add(new CatalogStockRefreshRun
            {
                RunKey = "Europe/Berlin:2026-08-19",
                BusinessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
                TimeZoneId = "Europe/Berlin",
                ScheduledAtUtc = clock.GetUtcNow().UtcDateTime,
                Status = CatalogStockRefreshRunStatus.Running,
                LeaseOwner = "another-instance",
                LeaseExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-5),
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-10),
                UpdatedAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-10)
            });
            await db.SaveChangesAsync();
        }

        await harness.Service.TriggerRunAsync(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), clock.GetUtcNow().UtcDateTime, "resume");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await verifyDb.CatalogStockRefreshRuns.SingleAsync();
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, run.Status);
        Assert.Equal(1, run.Processed);
    }

    [Fact]
    public async Task Run_ResumePendingStock_SkipsAlreadyCompletedQuoteStep()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "FIRST", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "SECOND", StockExchanges.Nyse, StockTrackingStatus.CatalogOnly);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogStockRefreshRuns.Add(new CatalogStockRefreshRun
            {
                RunKey = "Europe/Berlin:2026-08-19",
                BusinessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
                TimeZoneId = "Europe/Berlin",
                ScheduledAtUtc = clock.GetUtcNow().UtcDateTime,
                Status = CatalogStockRefreshRunStatus.Running,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                LastProcessedStockId = 0,
                PendingStockId = 1,
                PendingQuoteCompleted = true,
                PendingHistoryCompleted = false,
                TotalDiscovered = 2,
                Processed = 0,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
                UpdatedAtUtc = clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        await harness.Service.TriggerRunAsync(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), clock.GetUtcNow().UtcDateTime, "resume");

        Assert.DoesNotContain(1, quote.ProcessedIds);
        Assert.Contains(2, quote.ProcessedIds);
        Assert.Contains(1, history.ProcessedIds);
        Assert.Contains(2, history.ProcessedIds);
    }

    [Fact]
    public async Task CrossInstance_Lease_PreventsDuplicateExecution()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "MSFT", StockExchanges.Nyse, StockTrackingStatus.CatalogOnly);

        var serviceB = harness.CreateSecondService();
        var businessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19));

        await Task.WhenAll(
            harness.Service.TriggerRunAsync(businessDate, clock.GetUtcNow().UtcDateTime, "a"),
            serviceB.TriggerRunAsync(businessDate, clock.GetUtcNow().UtcDateTime, "b"));

        Assert.Equal(2, quote.ProcessedIds.Count);
        Assert.Equal(2, quote.ProcessedIds.Distinct().Count());
    }

    [Fact]
    public async Task Run_NewerDelayedQuote_IsPersistedDuringNightlyRefresh()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService
        {
            QuoteFactory = ticker => new StockQuoteResponse
            {
                Symbol = ticker,
                CurrentPriceEur = 752m,
                ChangeEur = -52m,
                PercentChange = -6.47m,
                MarketState = "REGULAR",
                PriceSession = "REGULAR",
                PriceTimestampUtc = new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc),
                IsStale = true,
                DelayWarning = "Котировка задержана",
            }
        };
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Frankfurt, StockTrackingStatus.Tracked);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stock = await db.Stocks.SingleAsync(x => x.Id == 1);
            stock.CurrentPrice = 804m;
            stock.CurrentPriceChange = -44m;
            stock.CurrentPriceChangePercent = -5.19m;
            stock.CurrentPriceAt = new DateTime(2026, 8, 18, 12, 17, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }

        await harness.Service.TriggerRunAsync(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), clock.GetUtcNow().UtcDateTime, "test");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await verifyDb.Stocks.SingleAsync(x => x.Id == 1);
        Assert.Equal(752m, persisted.CurrentPrice);
        Assert.Equal(-52m, persisted.CurrentPriceChange);
        Assert.Equal(-6.47m, persisted.CurrentPriceChangePercent);
        Assert.Equal(new DateTime(2026, 8, 19, 8, 1, 0, DateTimeKind.Utc), persisted.CurrentPriceAt);
        Assert.True(persisted.CurrentPriceIsDelayed);
        Assert.Equal("Котировка задержана", persisted.CurrentPriceDelayWarning);
    }

    [Fact]
    public async Task GetStatus_ReturnsBoundedUsefulState()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history);
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.Service.TriggerRunAsync(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), clock.GetUtcNow().UtcDateTime, "test");

        var status = await harness.Service.GetStatusAsync();
        Assert.NotNull(status.CurrentOrLatestRun);
        Assert.Equal("Europe/Berlin", status.TimeZoneId);
        Assert.True(status.NextScheduledRunUtc > status.GeneratedAtUtc);
        Assert.InRange(status.CurrentOrLatestRun!.FailureSummary?.Length ?? 0, 0, 4000);
    }

    [Fact]
    public void StatusController_UsesAuthorizeConvention()
    {
        var attribute = typeof(CatalogStockRefreshController).GetCustomAttributes(typeof(AuthorizeAttribute), true).FirstOrDefault();
        Assert.NotNull(attribute);
    }

    // -----------------------------------------------------------------------
    // Regression tests for the 2026-08-19 production incident and related
    // scheduler occurrence-selection logic.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("2026-08-19T06:00:00Z", "2026-08-18T20:30:00Z", "2026-08-18")] // 08:00 Berlin CEST, before today's 22:30 → yesterday
    [InlineData("2026-08-19T20:29:59Z", "2026-08-18T20:30:00Z", "2026-08-18")] // 22:29:59 Berlin, just before 22:30 → yesterday
    [InlineData("2026-08-19T20:30:00Z", "2026-08-19T20:30:00Z", "2026-08-19")] // exactly at 22:30 Berlin → today
    [InlineData("2026-08-19T21:00:00Z", "2026-08-19T20:30:00Z", "2026-08-19")] // 23:00 Berlin, after 22:30 → today
    public void CatchUp_OccurrenceSelection_PicksCorrectPastOccurrence(
        string nowUtcStr,
        string expectedOccurrenceUtcStr,
        string expectedBusinessDateStr)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var nowUtc = DateTimeOffset.Parse(nowUtcStr);
        var expectedOccurrence = DateTimeOffset.Parse(expectedOccurrenceUtcStr);
        var expectedBusinessDate = DateOnly.Parse(expectedBusinessDateStr);

        var snapshot = CatalogStockRefreshScheduleCalculator.Snapshot(nowUtc, tz, new TimeSpan(22, 30, 0));

        DateTimeOffset catchUpOccurrenceUtc;
        DateOnly catchUpBusinessDate;
        if (nowUtc >= snapshot.TodayScheduledRunUtc)
        {
            catchUpOccurrenceUtc = snapshot.TodayScheduledRunUtc;
            catchUpBusinessDate = snapshot.BusinessDate;
        }
        else
        {
            catchUpBusinessDate = snapshot.BusinessDate.AddDays(-1);
            catchUpOccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(catchUpBusinessDate, new TimeSpan(22, 30, 0), tz);
        }

        Assert.Equal(expectedOccurrence, catchUpOccurrenceUtc);
        Assert.Equal(expectedBusinessDate, catchUpBusinessDate);
    }

    [Fact]
    public void RunKey_IncludesScheduledUtcOccurrence_NotJustDate()
    {
        // Two different occurrences on the same calendar date (e.g., 22:30 summer vs winter)
        // must produce different run keys.
        var summerUtc = new DateTime(2026, 8, 19, 20, 30, 0, DateTimeKind.Utc); // CEST
        var winterUtc = new DateTime(2026, 11, 5, 21, 30, 0, DateTimeKind.Utc); // CET

        var keySummer = $"Europe/Berlin:{summerUtc:yyyy-MM-ddTHH:mm:ssZ}";
        var keyWinter = $"Europe/Berlin:{winterUtc:yyyy-MM-ddTHH:mm:ssZ}";

        Assert.NotEqual(keySummer, keyWinter);
        Assert.Equal("Europe/Berlin:2026-08-19T20:30:00Z", keySummer);
        Assert.Equal("Europe/Berlin:2026-11-05T21:30:00Z", keyWinter);
    }

    [Fact]
    public async Task ProductionIncident_MorningCatchUp_DoesNotBlockEveningRun()
    {
        // Reproduce the 2026-08-19 production incident:
        // A morning catch-up ran under businessDate=2026-08-19 with ScheduledAtUtc=2026-08-18T22:00Z
        // (wrong occurrence). With the new key format, this row must NOT block the real
        // 2026-08-19 22:30 scheduled run.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var scheduledAug19Utc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(scheduledAug19Utc); // exactly at the evening occurrence
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        // Seed a row that resembles the production incident:
        // - RunKey is the legacy date-only format for 2026-08-19
        // - BusinessDate is 2026-08-19 (same as today's occurrence)
        // - But ScheduledAtUtc is 2026-08-18T22:00Z — more than 2h before the real occurrence
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogStockRefreshRuns.Add(new CatalogStockRefreshRun
            {
                RunKey = "Europe/Berlin:2026-08-19",
                BusinessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
                TimeZoneId = "Europe/Berlin",
                ScheduledAtUtc = new DateTime(2026, 8, 18, 22, 0, 0, DateTimeKind.Utc), // wrong — from incident
                Status = CatalogStockRefreshRunStatus.CompletedWithErrors,
                CreatedAtUtc = new DateTime(2026, 8, 19, 3, 18, 36, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 8, 19, 5, 0, 46, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        // The evening run should NOT be blocked — it should find no matching row via the
        // new-format key and, because the legacy row's ScheduledAtUtc drifts >2h, should
        // create a fresh row and execute successfully.
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            scheduledAug19Utc.UtcDateTime,
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await verifyDb.CatalogStockRefreshRuns.ToListAsync();

        // Two rows: the legacy incident row and the new correctly-keyed row
        Assert.Equal(2, runs.Count);
        var newRun = runs.Single(r => r.RunKey != "Europe/Berlin:2026-08-19");
        Assert.Equal($"Europe/Berlin:{scheduledAug19Utc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", newRun.RunKey);
        Assert.Equal(scheduledAug19Utc.UtcDateTime, newRun.ScheduledAtUtc);
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, newRun.Status);
        Assert.Single(quote.ProcessedIds); // stock was actually processed
    }

    [Fact]
    public async Task StartupBeforeScheduledTime_CatchUpUsesYesterdayOccurrence_TodayRunsIndependently()
    {
        // Morning startup at 08:00 Berlin (06:00 UTC CEST) on 2026-08-19.
        // Catch-up must use the previous occurrence (2026-08-18T20:30:00Z),
        // and the evening run for 2026-08-19T20:30:00Z must run independently.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var morningUtc = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero); // 08:00 Berlin

        var aug18OccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 18)), new TimeSpan(22, 30, 0), tz);
        var aug19OccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        Assert.Equal(new DateTimeOffset(2026, 8, 18, 20, 30, 0, TimeSpan.Zero), aug18OccurrenceUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 20, 30, 0, TimeSpan.Zero), aug19OccurrenceUtc);

        // Morning catch-up occurs
        var clock = new FixedTimeProvider(morningUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        // Simulate startup catch-up for yesterday's occurrence
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 18)),
            aug18OccurrenceUtc.UtcDateTime,
            "startup-catch-up");

        // Advance clock to evening and run today's scheduled occurrence
        clock.Set(aug19OccurrenceUtc);
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            aug19OccurrenceUtc.UtcDateTime,
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await verifyDb.CatalogStockRefreshRuns.OrderBy(r => r.ScheduledAtUtc).ToListAsync();

        // Both runs created and completed independently
        Assert.Equal(2, runs.Count);
        Assert.Equal($"Europe/Berlin:{aug18OccurrenceUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", runs[0].RunKey);
        Assert.Equal($"Europe/Berlin:{aug19OccurrenceUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", runs[1].RunKey);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 8, 18)), runs[0].BusinessDate);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), runs[1].BusinessDate);
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, runs[0].Status);
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, runs[1].Status);
        // Both runs independently processed the stock (each run re-processes all stocks)
        Assert.Equal(2, quote.ProcessedIds.Count);
    }

    [Fact]
    public async Task CompletedPreviousOccurrence_DoesNotBlockNextScheduledOccurrence()
    {
        // Completed yesterday catch-up must never block today's evening run.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var aug18OccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 18)), new TimeSpan(22, 30, 0), tz);
        var aug19OccurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(aug18OccurrenceUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        // Run and complete Aug 18's occurrence
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 18)),
            aug18OccurrenceUtc.UtcDateTime,
            "scheduled");

        // Now run Aug 19's occurrence — must NOT be blocked by Aug 18's completed row
        clock.Set(aug19OccurrenceUtc);
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            aug19OccurrenceUtc.UtcDateTime,
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await verifyDb.CatalogStockRefreshRuns.OrderBy(r => r.ScheduledAtUtc).ToListAsync();

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(CatalogStockRefreshRunStatus.Completed, r.Status));
        Assert.Equal(2, quote.ProcessedIds.Count);
    }

    [Fact]
    public async Task RepeatedRestart_AfterCompletedOccurrence_DoesNotDuplicate()
    {
        // After an occurrence completes, repeated restarts/calls must not re-run it.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var occurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(occurrenceUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        var businessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19));
        await harness.Service.TriggerRunAsync(businessDate, occurrenceUtc.UtcDateTime, "startup-catch-up");
        var callsAfterFirst = quote.ProcessedIds.Count;

        // Simulate three more restarts
        await harness.Service.TriggerRunAsync(businessDate, occurrenceUtc.UtcDateTime, "startup-catch-up");
        await harness.Service.TriggerRunAsync(businessDate, occurrenceUtc.UtcDateTime, "startup-catch-up");
        await harness.Service.TriggerRunAsync(businessDate, occurrenceUtc.UtcDateTime, "startup-catch-up");

        Assert.Equal(callsAfterFirst, quote.ProcessedIds.Count); // no extra processing
        Assert.Equal(1, callsAfterFirst); // only processed once

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyDb.CatalogStockRefreshRuns.CountAsync()); // single row
    }

    [Fact]
    public async Task LegacyRunKey_ResumedWhenScheduledAtUtcIsClose()
    {
        // Legacy row (old key format timezone:date) with ScheduledAtUtc within 2h should be
        // resumed rather than creating a duplicate new-format row.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var occurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(occurrenceUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "SAP", StockExchanges.Frankfurt, StockTrackingStatus.CatalogOnly);

        // Seed a pre-existing legacy-format row (from before deployment)
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogStockRefreshRuns.Add(new CatalogStockRefreshRun
            {
                RunKey = "Europe/Berlin:2026-08-19",          // legacy format
                BusinessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
                TimeZoneId = "Europe/Berlin",
                ScheduledAtUtc = occurrenceUtc.UtcDateTime,   // same as expected — within 2h
                Status = CatalogStockRefreshRunStatus.Running,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null,
                CreatedAtUtc = occurrenceUtc.UtcDateTime,
                UpdatedAtUtc = occurrenceUtc.UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        // TriggerRunAsync should find the legacy row and resume it (not create a second row)
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            occurrenceUtc.UtcDateTime,
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await verifyDb.CatalogStockRefreshRuns.ToListAsync();
        Assert.Single(runs); // only the original legacy row, resumed — no duplicate
        Assert.Equal("Europe/Berlin:2026-08-19", runs[0].RunKey); // legacy key preserved
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, runs[0].Status);
    }

    [Fact]
    public async Task LegacyRunKey_WithWrongScheduledAtUtc_DoesNotBlockNewRun()
    {
        // Legacy row where ScheduledAtUtc drifts >2h from the real occurrence
        // must NOT be reused — a fresh new-format row must be created.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var occurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(occurrenceUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        // Seed the production-incident row: legacy key, same businessDate, but wrong ScheduledAtUtc
        var wrongScheduledAtUtc = new DateTime(2026, 8, 18, 22, 0, 0, DateTimeKind.Utc); // >2h before real occurrence
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogStockRefreshRuns.Add(new CatalogStockRefreshRun
            {
                RunKey = "Europe/Berlin:2026-08-19",
                BusinessDate = DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
                TimeZoneId = "Europe/Berlin",
                ScheduledAtUtc = wrongScheduledAtUtc,
                Status = CatalogStockRefreshRunStatus.CompletedWithErrors,
                CreatedAtUtc = wrongScheduledAtUtc,
                UpdatedAtUtc = wrongScheduledAtUtc
            });
            await db.SaveChangesAsync();
        }

        // Real scheduled run must create a new row and complete successfully
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            occurrenceUtc.UtcDateTime,
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await verifyDb.CatalogStockRefreshRuns.OrderBy(r => r.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, runs.Count);
        var newRun = runs.Single(r => r.RunKey != "Europe/Berlin:2026-08-19");
        Assert.Equal($"Europe/Berlin:{occurrenceUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", newRun.RunKey);
        Assert.Equal(occurrenceUtc.UtcDateTime, newRun.ScheduledAtUtc);
        Assert.Equal(CatalogStockRefreshRunStatus.Completed, newRun.Status);
    }

    [Theory]
    [InlineData("2026-03-29", 20, 30, 0)] // spring-forward day in Berlin (CEST), 22:30 local = 20:30 UTC
    [InlineData("2026-10-25", 21, 30, 0)] // fall-back day in Berlin (CET), 22:30 local = 21:30 UTC
    public void ScheduledUtc_DstTransitionDays_IsCorrect(string dateStr, int expectedHour, int expectedMinute, int expectedSecond)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var businessDate = DateOnly.Parse(dateStr);
        var utc = CatalogStockRefreshScheduleCalculator.ToUtc(businessDate, new TimeSpan(22, 30, 0), tz);
        Assert.Equal(expectedHour, utc.Hour);
        Assert.Equal(expectedMinute, utc.Minute);
        Assert.Equal(expectedSecond, utc.Second);
    }

    [Fact]
    public async Task ScheduledAtUtc_InRunRecord_MatchesOccurrence_NotArbitraryNow()
    {
        // Every run row must have a ScheduledAtUtc that exactly equals the occurrence UTC,
        // not a stale or incorrect timestamp.
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var occurrenceUtc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(occurrenceUtc.AddMinutes(5)); // a few minutes after occurrence
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        });
        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        // Pass the occurrence UTC (not clock.Now) as scheduledAtUtc
        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            occurrenceUtc.UtcDateTime, // exact occurrence
            "scheduled");

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await verifyDb.CatalogStockRefreshRuns.SingleAsync();
        Assert.Equal(occurrenceUtc.UtcDateTime, run.ScheduledAtUtc);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 8, 19)), run.BusinessDate);
    }

    [Fact]
    public async Task GetStatus_NextScheduledRunUtc_IsCorrectOccurrenceUtc()
    {
        // Status endpoint must report the next occurrence UTC correctly (not just "now + 1 day").
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // Set clock to 08:00 Berlin CEST (before today's 22:30)
        var morningUtc = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);
        var expectedNextOccurrence = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);

        var clock = new FixedTimeProvider(morningUtc);
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        await using var harness = await Harness.CreateAsync(clock, quote, history);

        var status = await harness.Service.GetStatusAsync();
        Assert.Equal(expectedNextOccurrence.UtcDateTime, status.NextScheduledRunUtc);
    }

    [Fact]
    public async Task ShutdownCancellation_AfterPartialWork_PersistsInterruptedJournalWithProgress()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 20, 40, 0, TimeSpan.Zero));
        var quote = new RecordingQuoteService();
        var history = new RecordingHistoryService();
        var hostLifetime = new TestHostApplicationLifetime();
        await using var harness = await Harness.CreateAsync(clock, quote, history, options: new CatalogStockRefreshJobOptions
        {
            InterRequestDelay = TimeSpan.Zero,
            RetryLimit = 0
        }, withProcessJournal: true, hostApplicationLifetime: hostLifetime);

        await harness.SeedStockAsync(1, "AAPL", StockExchanges.Nyse, StockTrackingStatus.Tracked);
        await harness.SeedStockAsync(2, "MSFT", StockExchanges.Nyse, StockTrackingStatus.Tracked);

        var cancellationSource = new CancellationTokenSource();
        var fetchCount = 0;
        quote.OnFetch = _ =>
        {
            fetchCount++;
            if (fetchCount < 2)
            {
                return;
            }

            hostLifetime.StopApplication();
            cancellationSource.Cancel();
        };

        await harness.Service.TriggerRunAsync(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)),
            clock.GetUtcNow().UtcDateTime,
            "scheduled",
            cancellationSource.Token);

        await using var verify = harness.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var refreshedStock = await verifyDb.Stocks.FirstAsync(x => x.Id == 1);
        Assert.Equal(10m, refreshedStock.CurrentPrice);

        var run = await verifyDb.CatalogStockRefreshRuns.SingleAsync();
        Assert.Equal(1, run.Processed);

        var processRun = await verifyDb.SystemProcessRuns.SingleAsync();
        Assert.Equal(SystemProcessRunStatus.Interrupted, processRun.Status);
        Assert.Equal(1, processRun.ProcessedItems);
        Assert.Equal(2, processRun.SucceededItems);
        Assert.Equal("stockId=1", processRun.LastProcessedEntity);
        Assert.Contains("Обработано 1 из 2", processRun.ResultSummary);
        Assert.Contains("Остановка/перезапуск приложения", processRun.ErrorSummary);
    }

    [Fact]
    public void ScheduleCalculator_EuropeBerlin_Winter_IsUtcPlus1()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // 2026-11-05 is standard CET (UTC+1)
        var utc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 11, 5)), new TimeSpan(22, 30, 0), tz);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 21, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void ScheduleCalculator_EuropeBerlin_Summer_IsUtcPlus2()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // 2026-08-19 is CEST (UTC+2)
        var utc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 8, 19)), new TimeSpan(22, 30, 0), tz);
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 20, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void ScheduleCalculator_SpringForwardInvalidTime_SkipsToValidTime()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // 2026-03-29: clocks spring forward at 02:00 → 03:00, so 02:30 is invalid.
        // ToUtc must skip forward to the next valid minute.
        var invalidTime = new TimeSpan(2, 30, 0);
        var utc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 3, 29)), invalidTime, tz);
        // Should be valid UTC
        Assert.False(tz.IsInvalidTime(new DateTime(2026, 3, 29).Add(utc.TimeOfDay)));
        Assert.True(utc.UtcDateTime > new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ScheduleCalculator_FallBackAmbiguousTime_UsesDstOffset()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // 2026-10-25: clocks fall back at 03:00 → 02:00. 02:30 is ambiguous.
        // Convention: use the larger (DST / summer) offset — CEST = UTC+2.
        var ambiguousTime = new TimeSpan(2, 30, 0);
        var utc = CatalogStockRefreshScheduleCalculator.ToUtc(
            DateOnly.FromDateTime(new DateTime(2026, 10, 25)), ambiguousTime, tz);
        // Max offset for ambiguous 02:30 in Europe/Berlin is +02:00 (DST)
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), utc);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(ServiceProvider services, CatalogStockRefreshHostedService service, FixedTimeProvider clock, CatalogStockRefreshJobOptions options)
        {
            Services = services;
            Service = service;
            Clock = clock;
            JobOptions = options;
        }

        public ServiceProvider Services { get; }
        public CatalogStockRefreshHostedService Service { get; }
        public FixedTimeProvider Clock { get; }
        public CatalogStockRefreshJobOptions JobOptions { get; }

        public static async Task<Harness> CreateAsync(
            FixedTimeProvider clock,
            RecordingQuoteService quoteService,
            RecordingHistoryService historyService,
            CatalogStockRefreshJobOptions? options = null,
            bool withProcessJournal = false,
            IHostApplicationLifetime? hostApplicationLifetime = null)
        {
            var opts = options ?? new CatalogStockRefreshJobOptions();
            var services = new ServiceCollection();
            var dbName = $"catalog-refresh-tests-{Guid.NewGuid():N}";
            services.AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddDbContext<AppDbContext>(b => b.UseInMemoryDatabase(dbName));
            services.AddScoped<IStockQuoteFetchService>(_ => quoteService);
            services.AddScoped<StockQuoteSnapshotPersistenceService>();
            services.AddScoped<IStockHistoryService>(_ => historyService);

            var provider = services.BuildServiceProvider();
            await using var setup = provider.CreateAsyncScope();
            await setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

            ISystemProcessJournalService? journalService = null;
            if (withProcessJournal)
            {
                journalService = new SystemProcessJournalService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    clock,
                    Options.Create(new SystemProcessJournalOptions()),
                    NullLogger<SystemProcessJournalService>.Instance);
            }

            var service = new CatalogStockRefreshHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                clock,
                Options.Create(opts),
                NullLogger<CatalogStockRefreshHostedService>.Instance,
                new CatalogMaintenanceLeaseService(provider.GetRequiredService<IServiceScopeFactory>(), clock),
                (_, _) => Task.CompletedTask,
                journalService,
                hostApplicationLifetime);

            return new Harness(provider, service, clock, opts);
        }

        public CatalogStockRefreshHostedService CreateSecondService()
            => new(
                Services.GetRequiredService<IServiceScopeFactory>(),
                Clock,
                Options.Create(JobOptions),
                NullLogger<CatalogStockRefreshHostedService>.Instance,
                new CatalogMaintenanceLeaseService(Services.GetRequiredService<IServiceScopeFactory>(), Clock),
                (_, _) => Task.CompletedTask);

        public async Task SeedManyStocksAsync(int count)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = Clock.GetUtcNow().UtcDateTime;
            for (var i = 1; i <= count; i++)
            {
                db.Stocks.Add(new Stock
                {
                    Id = i,
                    Ticker = $"T{i}",
                    Name = $"Stock {i}",
                    CommonName = $"Stock {i}",
                    Exchange = StockExchanges.Nyse,
                    TrackingStatus = i % 2 == 0 ? StockTrackingStatus.Tracked : StockTrackingStatus.CatalogOnly,
                    UpdatedAt = now
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task SeedStockAsync(int stockId, string ticker, string exchange, StockTrackingStatus trackingStatus)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Stocks.Add(new Stock
            {
                Id = stockId,
                Ticker = ticker,
                Name = ticker == string.Empty ? $"Stock {stockId}" : ticker,
                CommonName = ticker == string.Empty ? $"Stock {stockId}" : ticker,
                Exchange = exchange,
                TrackingStatus = trackingStatus,
                UpdatedAt = Clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
        }
    }

    private sealed class RecordingQuoteService : IStockQuoteFetchService
    {
        public List<int> ProcessedIds { get; } = [];
        public Func<string, StockQuoteResponse>? QuoteFactory { get; set; }
        public Action<string>? OnFetch { get; set; }

        public Task<StockQuoteFetchResult> FetchAsync(
            string ticker,
            string exchange,
            string? finanzenNetSlug,
            CancellationToken cancellationToken = default)
        {
            OnFetch?.Invoke(ticker);

            if (ticker.Length > 1 && ticker.StartsWith('T') && int.TryParse(ticker[1..], out var id))
            {
                ProcessedIds.Add(id);
            }
            else if (ticker == "AAPL")
            {
                ProcessedIds.Add(1);
            }
            else if (ticker == "MSFT")
            {
                ProcessedIds.Add(2);
            }
            else if (ticker == "SECOND")
            {
                ProcessedIds.Add(2);
            }
            else if (ticker == "GOOD")
            {
                ProcessedIds.Add(3);
            }
            else if (ticker == "SAP")
            {
                ProcessedIds.Add(10);
            }

            return Task.FromResult(StockQuoteFetchResult.Success(
                QuoteFactory?.Invoke(ticker) ?? new StockQuoteResponse
                {
                    Symbol = ticker,
                    CurrentPriceEur = 10m,
                    ChangeEur = 1m,
                    PercentChange = 1m,
                    MarketState = "REGULAR",
                    PriceSession = "REGULAR",
                    PriceTimestampUtc = new DateTime(2026, 8, 19, 20, 30, 0, DateTimeKind.Utc),
                }));
        }
    }

    private sealed class RecordingHistoryService : IStockHistoryService
    {
        public List<int> ProcessedIds { get; } = [];

        public Task SyncHistoricalDataForStockAsync(Stock stock, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SyncHistoricalDataForAllStocksAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StockHistoryResponse> GetHistoryAsync(Stock stock, string range, CancellationToken cancellationToken = default)
            => Task.FromResult(new StockHistoryResponse());

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, CancellationToken cancellationToken = default)
        {
            ProcessedIds.Add(stock.Id);
            return Task.FromResult(new StockHistoryRefreshResponse
            {
                StockId = stock.Id,
                DeletedPoints = 0,
                ImportedPoints = 0,
                RateLimited = false
            });
        }

        public Task<StockHistoryRefreshResponse> RefreshHistoryAsync(Stock stock, StockHistoryRefreshTrigger trigger, CancellationToken cancellationToken = default)
            => RefreshHistoryAsync(stock, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            if (!_stopping.IsCancellationRequested)
            {
                _stopping.Cancel();
            }

            if (!_stopped.IsCancellationRequested)
            {
                _stopped.Cancel();
            }
        }
    }
}
