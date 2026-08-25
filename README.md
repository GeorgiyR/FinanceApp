# FinanceApp

Finance portfolio management application with ASP.NET Core 8 Backend and React Frontend.

## Backend

The backend is built with ASP.NET Core 8.

### Requirements
- .NET 8 SDK
- MySQL / MariaDB

### Local Configuration

`appsettings.json` in this repository contains **only empty placeholders** — no real credentials are stored in source control.

Supply real values via environment variables (recommended) or [.NET user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets):

**Environment variables (recommended for CI/Docker):**
```bash
export ConnectionStrings__DefaultConnection="server=localhost;port=3306;database=financeapp;user=financeapp_user;******"
export Jwt__Key="YOUR_JWT_SIGNING_KEY_AT_LEAST_32_CHARS"
export Finnhub__ApiKey="YOUR_FINNHUB_KEY"
export YahooFinance__MinRequestInterval="00:00:01.500"
export YahooFinance__CooldownDuration="00:30:00"
```

**User secrets (recommended for local development):**
```bash
cd FinanceApp.API
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "server=localhost;port=3306;database=financeapp;user=financeapp_user;******"
dotnet user-secrets set "Jwt:Key" "YOUR_JWT_SIGNING_KEY_AT_LEAST_32_CHARS"
dotnet user-secrets set "Finnhub:ApiKey" "YOUR_FINNHUB_KEY"
```

> ⚠️ **Credential rotation notice:** Previously committed versions of `appsettings.json` contained a database password, JWT signing key, and Finnhub API key. Those values **must be considered compromised** and rotated at the infrastructure level. Deleting them from the latest commit does **not** remove them from Git history — anyone with repository access can recover them from past commits. Rotate all three credentials outside the repository immediately.

### Run
```bash
cd FinanceApp.API
dotnet run
```

### Account login and migration notes

- Login now accepts one identifier field: **username or email** (case-insensitive, with trim).
- Legacy mobile payload `{ email, password }` is still accepted by `/api/Auth/login`.
- Password hashes are stored with ASP.NET Core `PasswordHasher` (PBKDF2). Existing legacy SHA-256 hashes are upgraded automatically on successful login.
- Username rules (validated on backend): 3-32 chars, only letters/digits/`. _ -`, no `@`, case-insensitive unique.

Before applying migration `AddNormalizedUserAuth` to existing production data, run a preflight check for:
- duplicate usernames ignoring case/whitespace;
- usernames conflicting with any user email ignoring case/whitespace;
- usernames/emails exceeding the new lengths.

If conflicts exist, migration should be stopped and data corrected manually before retry.

### Yahoo Finance throttling and cooldown

FinanceApp routes all Yahoo Finance quote and chart requests through one shared, process-wide coordinator.

| Option | Default | Description |
|---|---|---|
| `YahooFinance:MinRequestInterval` | `00:00:01.500` | Minimum delay between Yahoo request starts across the entire backend process. |
| `YahooFinance:CooldownDuration` | `00:30:00` | Shared fallback cooldown activated for all Yahoo callers after HTTP 429 when `Retry-After` is absent or invalid. |
| `YahooFinance:QuoteCacheDuration` | `00:00:10` | Short-term in-memory cache for successful Yahoo quote responses. Provider timestamps are preserved so cached quotes are not presented as freshly sourced. |
| `YahooFinance:RequestTimeout` | `00:00:10` | Per-request HTTP timeout for Yahoo calls. |

Notes:
- Yahoo concurrency is limited to **1** process-wide.
- Current quotes and historical refreshes share the same throttle, cooldown, and in-flight request coalescing.
- When Yahoo returns HTTP `429`, FinanceApp stops sending new Yahoo requests until the shared cooldown expires.

### Phase 1 technical-analysis schema and deployment notes

This repository now supports **multiple exchange listings sharing the same ISIN** and persists
**Yahoo adjusted close** alongside the existing raw close.

- `Stocks.Isin` is now indexed for lookup but is **no longer globally unique**.
- `StockHistoricalPrices.AdjustedClose` is nullable and is populated only when Yahoo
  `indicators.adjclose[0].adjclose` is present, aligned to the candle array, and strictly positive.
- `StockHistoricalPrices.Close` remains the canonical raw/unadjusted close for auditability and
  compatibility.
- Close-based technical indicators prefer adjusted close only when the required full sequence/window
  is complete and valid; otherwise that metric explicitly falls back to raw close. ATR remains
  raw-OHLC-based because adjusted OHLC is not available in the current model.
- Same-ISIN fallback remains **exact normalized ISIN only** (`TRIM + UPPER` plus ISIN format validation).
  No fallback by ticker or company name is used.

#### Read-only preflight checks before applying migration `20260820043053_AddAdjustedCloseAndNonUniqueIsin`

Review accidental duplicate listing identities first. This migration does **not** auto-delete,
rewrite, or merge stock rows.

```sql
-- Potential duplicate concrete listings by normalized ticker+exchange
SELECT
    UPPER(TRIM(Ticker)) AS NormalizedTicker,
    UPPER(TRIM(Exchange)) AS NormalizedExchange,
    COUNT(*) AS DuplicateCount,
    GROUP_CONCAT(Id ORDER BY Id) AS StockIds
FROM Stocks
GROUP BY UPPER(TRIM(Ticker)), UPPER(TRIM(Exchange))
HAVING COUNT(*) > 1;

-- Potential duplicate provider-symbol identities when ProviderSymbol is present
SELECT
    UPPER(TRIM(ProviderSymbol)) AS NormalizedProviderSymbol,
    COUNT(*) AS DuplicateCount,
    GROUP_CONCAT(Id ORDER BY Id) AS StockIds
FROM Stocks
WHERE ProviderSymbol IS NOT NULL AND TRIM(ProviderSymbol) <> ''
GROUP BY UPPER(TRIM(ProviderSymbol))
HAVING COUNT(*) > 1;
```

#### Recommended deployment order

1. Back up the database.
2. Run the preflight queries above and resolve unexpected duplicate concrete listings manually.
3. Apply EF migration `20260820043053_AddAdjustedCloseAndNonUniqueIsin`.
4. Deploy/restart the backend.
5. Refresh history for the affected stocks/index constituents to backfill `AdjustedClose` gradually.

#### Compatibility and rollback

- Existing rows remain valid with `AdjustedClose = NULL` until each listing is refreshed.
- The application keeps concrete listing deduplication at the application layer (`ProviderSymbol`,
  then `Ticker + Exchange`) to avoid imposing a potentially unsafe new production uniqueness rule
  without a dedicated data-cleanup rollout.
- Rolling back the application is safe; rolling back the migration removes `AdjustedClose` and
  restores the previous unique ISIN index, so it should only be done before any newly allowed
  same-ISIN multi-listing rows are inserted.

### Multi-horizon technical analysis API (read-only, authenticated)

Endpoint: `GET /api/stocks/{id}/technical-analysis`

This endpoint returns analytical information (not personalized investment advice) for four horizons in one payload:

- `ThreeMonths`
- `SixMonths`
- `OneYear`
- `TwoYears`

The endpoint is **read-only over persisted data**:

- uses stored daily history and persisted fundamentals snapshots;
- never triggers provider requests;
- never mutates history refresh scheduling state.

#### Response highlights

- stock identifiers (`Id`, `Ticker`, `Name`, `CommonName`, `Exchange`, `ISIN`, `WKN`);
- latest candle `AsOfUtc`;
- `IsPotentiallyStale`;
- history cadence/scheduling metadata (`HistoryRefreshCadence`, last-success and next-run timestamps);
- common metrics (`SMA20/50/200`, `EMA12/26`, `RSI14`, `MACD`, 1m/3m/6m/1y returns, volatility, drawdown, `ATR14`, adjusted-coverage);
- per-horizon:
  - `Score` (0..100),
  - `Signal` (`StrongBullish`, `ModeratelyBullish`, `Neutral`, `ModeratelyBearish`, `StrongBearish`),
  - `Confidence` (0..1),
  - component scores and explicit weights,
  - positive/negative factors and warnings.

#### Signal thresholds

- `80..100` → `StrongBullish`
- `65..79.999...` → `ModeratelyBullish`
- `45..64.999...` → `Neutral`
- `30..44.999...` → `ModeratelyBearish`
- `0..29.999...` → `StrongBearish`

#### Component weights (must sum to 1.0 per horizon)

| Component | 3 months | 6 months | 1 year | 2 years |
|---|---:|---:|---:|---:|
| Trend | 0.35 | 0.35 | 0.30 | 0.15 |
| Momentum | 0.35 | 0.25 | 0.15 | 0.05 |
| Returns | 0.20 | 0.20 | 0.20 | 0.15 |
| Risk | 0.10 | 0.15 | 0.15 | 0.20 |
| Fundamentals | 0.00 | 0.05 | 0.20 | 0.45 |

Missing component handling is deterministic: the final score renormalizes to available component weights, emits warnings, and lowers confidence.

#### Price/indicator semantics

- Close-based calculations prefer `AdjustedClose` per candle and fall back to raw `Close` for that candle when adjusted data is unavailable.
- `ATR14` uses unadjusted OHLC only (adjusted OHLC is not persisted).
- Candles are sorted/deduplicated deterministically by timestamp (last record wins on duplicate timestamp).

#### Confidence semantics

`Confidence` is data sufficiency/quality (not signal strength) and combines:

- horizon history coverage;
- latest-candle freshness;
- adjusted-close coverage;
- component availability/missingness;
- long-horizon fundamentals availability/freshness.

Long-horizon limitations are explicitly surfaced:

- if historical fundamentals trend coverage is insufficient, `TwoYears` includes warning code
  `FUNDAMENTAL_HISTORY_INSUFFICIENT`;
- stale/missing fundamentals lower `OneYear`/`TwoYears` confidence.

#### Performance notes

- EF queries use `AsNoTracking`.
- Only required columns are selected.
- Daily-history read is bounded (`Take(800)`), then normalized in-memory.

#### Local verification (no provider calls required)

```bash
dotnet build FinanceApp.sln
dotnet test FinanceApp.Core.Tests/FinanceApp.Core.Tests.csproj --filter FullyQualifiedName~StockTechnicalAnalysisScoringTests
```

### Nightly catalog refresh (Tracked + CatalogOnly)

FinanceApp now has a separate nightly background maintenance job that refreshes **quote + history** for all durable catalog stocks (`Tracked` and `CatalogOnly`) once per business day.

- Frequent/intraday automatic refresh behavior for tracked stocks remains unchanged.
- Nightly catalog job does **not** make `CatalogOnly` participate in the frequent tracked-only loop.
- Default schedule: `22:30:00` in `Europe/Berlin` local time (DST-aware).
- Fundamentals are intentionally **not** part of this loop.

Configuration (`CatalogStockRefreshJob` in `appsettings`):

| Option | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enables/disables nightly catalog refresh. |
| `RunCatchUpOnStartup` | `true` | If app was down at scheduled time, run one bounded catch-up on startup for today when missing. |
| `TimeZoneId` | `Europe/Berlin` | IANA/OS timezone used for local schedule and business date. |
| `LocalScheduleTime` | `22:30:00` | Local daily start time. |
| `BatchSize` | `40` | Deterministic stock page size. |
| `MaxConcurrency` | `1` | Processing concurrency limit. |
| `InterRequestDelay` | `00:00:00.250` | Delay between stock requests. |
| `RateLimitCooldown` | `00:02:00` | Cooldown pause used when provider rate-limits. |
| `RetryLimit` | `2` | Retry attempts per quote/history step (except hard rate-limit response). |
| `RetryBaseDelay` | `00:00:02` | Exponential retry base delay (with jitter). |
| `LeaseDuration` | `00:10:00` | Durable run lease duration for cross-instance safety. |
| `LeaseRenewInterval` | `00:02:00` | Lease renewal interval while run is active. |
| `SharedLeaseRetryDelay` | `00:00:30` | Delay before retrying when shared all-catalog maintenance lease is busy. |
| `ProgressLogEveryStocks` | `25` | Periodic progress logging cadence. |

Operational status endpoint (authenticated): `GET /api/catalog-refresh/status`

Migration: `20260819024150_AddCatalogStockRefreshRunState`

#### Corrective schema note for occurrence-keyed runs (PR #172 follow-up)

Root cause: PR #172 switched scheduler identity to occurrence-keyed `RunKey` (`{timeZoneId}:{scheduledUtc}`),
but the original DB uniqueness on `(BusinessDate, TimeZoneId)` still allowed only one row per date/timezone.
That made a legacy malformed row for `2026-08-19` block insertion of the correctly keyed occurrence row.

Corrected index semantics (migration `20260820072805_FixCatalogStockRefreshRunOccurrenceIndex`):
- `IX_CatalogStockRefreshRuns_RunKey` stays **unique** (idempotency of exact occurrence).
- `IX_CatalogStockRefreshRuns_BusinessDate_TimeZoneId` is now **non-unique** (lookup/supporting index only).
- `CatalogFundamentalsRefreshRuns` weekly uniqueness remains unchanged.

Read-only preflight (safe to run before deployment):

```sql
SHOW INDEX FROM CatalogStockRefreshRuns
WHERE Key_name IN (
  'IX_CatalogStockRefreshRuns_RunKey',
  'IX_CatalogStockRefreshRuns_BusinessDate_TimeZoneId'
);
```

Expected post-migration:
- `IX_CatalogStockRefreshRuns_RunKey` => `Non_unique = 0`
- `IX_CatalogStockRefreshRuns_BusinessDate_TimeZoneId` => `Non_unique = 1`

Manually corrected production handling:
- The corrective migration uses `DROP INDEX IF EXISTS` for
  `IX_CatalogStockRefreshRuns_BusinessDate_TimeZoneId`, so environments where that index was already
  replaced manually with the same name are safe.
- If manual correction used a different custom index name, keep it for the deployment window, apply this
  migration, then reconcile extra custom indexes in a separate controlled DBA change.
- No run rows are deleted/rewritten and no `RunKey` values are altered by this migration.

Recommended deployment order:
1. Back up database.
2. Run the preflight query above.
3. Apply migrations (including `20260820072805_FixCatalogStockRefreshRunOccurrenceIndex`).
4. Deploy/restart backend.
5. Re-run the preflight query and confirm expected index uniqueness flags.

Rollback caveat:
- Rolling back binaries is safe.
- Rolling back this migration attempts to restore unique `(BusinessDate, TimeZoneId)` and can fail if
  multiple rows now exist for the same date/timezone (which is expected after the fix). Treat DB rollback
  as a controlled/manual operation in that case.

### Multi-tier stock history refresh strategy

History refresh now uses tiered windows and durable per-stock schedule metadata instead of forcing a long-range fetch for every catalog stock on every pass.

- **Incremental** (default lookback: `10` calendar days): overlapping near-term upsert for daily candles (`OHLC`, `Volume`, `AdjustedClose`, currency/source metadata).
- **Reconciliation** (default lookback: `183` days): wider repair pass for provider corrections and late backfills.
- **FullBackfill** (default lookback: `1830` days): broad re-sync for initial/no-history stocks and explicit/manual refreshes, covering the advertised 5-year chart range.

Cadence policy is stored on `Stocks.HistoryRefreshCadence`:
- `Daily` (default for `Tracked`)
- `Weekly` (default for `CatalogOnly`)
- `Disabled` (excluded from automatic history refresh)

Durable scheduler fields:
- `LastIncrementalHistoryRefreshSucceededAtUtc`
- `NextIncrementalHistoryRefreshAtUtc`
- `LastHistoryReconciliationSucceededAtUtc`
- `NextHistoryReconciliationAtUtc`
- `LastFullHistoryBackfillSucceededAtUtc`
- `NextFullHistoryBackfillAtUtc`

Precedence and retries:
- one provider history request per stock/pass using `FullBackfill > Reconciliation > Incremental`;
- failures and rate limits schedule bounded retry via `TransientFailureRetryDelay` (default `02:00:00`);
- success timestamps advance **only** on successful provider fetch + persistence.

Distribution / deployment behavior:
- migration `20260820094806_AddStockHistoryRefreshCadenceAndScheduling` backfills cadence from `TrackingStatus` (`Tracked => Daily`, `CatalogOnly => Weekly`);
- initial due timestamps are deterministically spread using stock `Id` over day/week/month windows to avoid rollout spikes;
- stocks without daily history are made immediately due for initial full backfill (unless cadence is `Disabled`).

Indexes added for due-work selection:
- `IX_Stocks_HistoryRefreshCadence_NextIncremental_Id`
- `IX_Stocks_HistoryRefreshCadence_NextReconciliation_Id`
- `IX_Stocks_HistoryRefreshCadence_NextFullBackfill_Id`

Configuration (`StockHistoryRefresh` in `appsettings`):

| Option | Default |
|---|---|
| `IncrementalLookbackDays` | `10` |
| `ReconciliationLookbackDays` | `183` |
| `FullBackfillLookbackDays` | `1830` |
| `IncrementalDailyCadence` | `1.00:00:00` |
| `IncrementalWeeklyCadence` | `7.00:00:00` |
| `ReconciliationTrackedCadence` | `7.00:00:00` |
| `ReconciliationCatalogCadence` | `30.00:00:00` |
| `FullBackfillTrackedCadence` | `30.00:00:00` |
| `FullBackfillCatalogCadence` | `30.00:00:00` |
| `TransientFailureRetryDelay` | `02:00:00` |
| `MaxAutomaticStocksPerRun` | `100` |

Operational verification after deploy:
1. backup DB;
2. apply migrations;
3. deploy/restart backend;
4. verify `Stocks` new cadence/schedule columns are populated and due indexes exist;
5. inspect logs for tiered messages (`tier=Incremental|Reconciliation|FullBackfill`) and `not due` skips.

Rollback caveat:
- binary rollback is safe;
- DB rollback drops cadence/schedule columns and due indexes, so do it only as a controlled migration rollback.
- future portfolio/watchlist/index/manual-priority and corporate-action-triggered full backfills can extend this model by overriding cadence/tier selection without schema redesign.

### Weekly catalog fundamentals refresh (Tracked + CatalogOnly)

FinanceApp also runs a separate weekly maintenance job that refreshes **fundamental data only** for all durable catalog stocks (`Tracked` and `CatalogOnly`).

- Default schedule: **Sunday `02:30:00`** in `Europe/Berlin` local time.
- DST-safe behavior:
  - if local `02:30` is invalid during spring-forward, the job advances to the first valid local instant (`03:00`);
  - if local `02:30` is ambiguous during fall-back, the earlier UTC instant is used deterministically.
- Startup catch-up is bounded and idempotent: when startup happens after this week’s scheduled time and the weekly run is missing, one catch-up run is created for the current business week.
- Job is checkpointed and resumable by stock id cursor, uses deterministic paging, and skips stocks whose fundamentals are fresher than the configured threshold.
- Quote/history nightly job and weekly fundamentals job coordinate through a shared durable DB lease (`CatalogMaintenanceLeases`) so heavy all-catalog jobs do not overlap.

Configuration (`CatalogFundamentalsRefreshJob` in `appsettings`):

| Option | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enables/disables weekly fundamentals refresh. |
| `Weekday` | `Sunday` | Local weekday for weekly run. |
| `LocalScheduleTime` | `02:30:00` | Local weekly start time. |
| `TimeZoneId` | `Europe/Berlin` | Timezone used for schedule and business week key. |
| `RunCatchUpOnStartup` | `true` | Run one bounded catch-up when the current weekly run is missing. |
| `FreshnessThreshold` | `7.00:00:00` | Skip fundamentals refreshed more recently than this threshold. |
| `BatchSize` | `40` | Deterministic stock page size. |
| `MaxConcurrency` | `1` | Bounded processing concurrency (default low/safe). |
| `InterRequestDelay` | `00:00:00.250` | Delay between stock refresh attempts. |
| `RetryLimit` | `2` | Retry attempts for per-stock transient failures (rate-limit is handled separately). |
| `RetryBaseDelay` | `00:00:02` | Exponential retry base delay with jitter. |
| `ProviderRateLimitCooldown` | `00:02:00` | Cooldown pause after provider rate-limit responses. |
| `LeaseDuration` | `00:10:00` | Durable weekly run lease duration. |
| `LeaseRenewInterval` | `00:02:00` | Lease renewal interval while running. |
| `SharedLeaseRetryDelay` | `00:00:30` | Delay before retrying when shared all-catalog maintenance lease is busy. |
| `ProgressLogEveryStocks` | `25` | Progress logging cadence. |

Operational status endpoint (authenticated): `GET /api/catalog-fundamentals-refresh/status`

Migration: `20260819034334_AddWeeklyCatalogFundamentalsRefreshRunState`

Recommended production deployment order:
1. Backup database
2. Apply migrations
3. Deploy/restart backend

Rollback note: rolling back application binaries may leave `CatalogStockRefreshRuns`, `CatalogFundamentalsRefreshRuns`, and `CatalogMaintenanceLeases` unused; keeping them is safe and non-destructive.

### Index constituents import sources (DJIA + NASDAQ-100 + S&P 500 + DAX)

- Supported index-constituent imports:
  - `DJIA` → `FinanceApp.API/Data/index-constituents/djia.curated.snapshot.json`
  - `NDX` → `FinanceApp.API/Data/index-constituents/nasdaq100.curated.snapshot.json`
  - `SPX` → `FinanceApp.API/Data/index-constituents/sp500.curated.snapshot.json`
  - `DAX` → `FinanceApp.API/Data/index-constituents/dax.curated.snapshot.json`
- Not supported (return `422 Unsupported`): all other indices (EURO STOXX, FTSE, CAC, Nikkei, MSCI, etc.).
- Source attribution:
  - DJIA: `https://www.spglobal.com/spdji/en/indices/equity/dow-jones-industrial-average/`
  - NASDAQ-100: `https://www.nasdaq.com/market-activity/quotes/ndx-index`
  - S&P 500: `https://www.spglobal.com/spdji/en/indices/equity/sp-500/`
  - DAX: `https://www.dax-indices.com/index-details?isin=DE0008469008` (Deutsche Börse / STOXX Ltd.)
- Snapshot metadata includes:
  - `asOfDate` (verified snapshot date),
  - source URL,
  - curated flag (UI shows **"Проверенный снимок"**, not live feed).
- Current curated snapshot date for all four files in this repository: `2026-08-16`.
- S&P 500 note: the index contains 500 companies but **503 securities** in this snapshot, because
  Berkshire Hathaway (`BRK.A`/`BRK.B`), Brown-Forman (`BF.A`/`BF.B`), and Alphabet (`GOOGL`/`GOOG`)
  each have two share classes listed separately (500 companies + 3 extra class lines = 503). Class-share tickers that contain a dot (e.g. `BRK.B`) use the Yahoo Finance
  provider-symbol convention with a hyphen (`BRK-B`) in the `providerSymbol` field so that quote
  lookups work correctly; the internal `ticker` field keeps the canonical dot notation.
- DAX notes:
  - DAX contains **40 components** as of the snapshot date.
  - All DAX stocks are listed on Frankfurt / Xetra; exchange is mapped to `Frankfurt` (existing `StockExchanges.Frankfurt` constant).
  - Provider symbols use the Yahoo Finance `.DE` suffix convention (e.g. `SAP.DE`, `SIE.DE`, `RHM.DE`).
    The internal `ticker` field uses the bare Xetra ticker without the suffix (e.g. `SAP`, `SIE`, `RHM`).
  - Special ticker cases: numeric prefix (`1COV`), numeric suffix for preference shares (`VOW3`, `HEN3`, `MUV2`, `SRT3`, `HNR1`), and Porsche AG (`P911`).
  - Merck KGaA (`MRK.DE`, Frankfurt) is entirely distinct from Merck & Co. (`MRK`, NYSE) — different exchange, different providerSymbol, and different ISIN.
  - ISIN is provided where the issuer is a German SE incorporated in Germany (`DE…` prefix). Non-German-incorporated constituents (Airbus SE, Qiagen NV) have `isin: null` because they carry non-`DE` ISINs not confirmed in this snapshot.
  - No WKN values are stored; the existing data model does not include WKN without schema changes.
  - DAX is a **total-return index** (includes reinvested dividends). This is a data-quality note only; it does not affect constituent import.
  - Routing is by normalized canonical code `DAX` only; `DAX 40`, `Deutscher Aktienindex`, and `^GDAXI` are not accepted as routing keys.
- Why curated snapshots:
  - No free/public stable structured runtime endpoint was available without keys/secrets and with
    clear production-safe usage constraints.
  - S&P 500 official component lists require a commercial data license from S&P Dow Jones Indices;
    the curated snapshot is used instead.
  - Deutsche Börse / STOXX Ltd. maintain the official DAX composition but do not expose a free machine-readable public API endpoint; the curated snapshot is used instead.
- Explicit non-goals:
  - ETF holdings (`DIA`, `QQQ`, `SPY`, `EWG` or any other ETF) are **not** used as a substitute for official index constituents.
  - `CatalogOnly` imports do **not** auto-enable quote/history/fundamentals tracking.
  - This PR does not add MDAX, SDAX, TecDAX, EURO STOXX 50, FTSE 100, CAC 40, Nikkei, or MSCI support.
- Manual verification/update workflow:
  1. Verify current constituent lists from authoritative index-owner sources (see source URLs above).
  2. For DAX, visit `https://www.dax-indices.com/index-details?isin=DE0008469008` and confirm the 40 current components.
  3. Update the relevant curated snapshot JSON with confirmed ticker/name/exchange (and ISIN only when
     reliably sourced — set to `null` otherwise).
  4. Update `asOfDate`, keep source URL attribution, and ensure identity uniqueness (`providerSymbol|exchange`).
  5. Run backend/frontend tests and build.
  6. Open a PR describing source provenance, as-of date, snapshot entry count, and any known caveats.

---

## Frontend

A React + TypeScript SPA that connects to the backend API.

### Technologies
- React 18, TypeScript, Vite
- Ant Design 5, Recharts, Axios, Day.js

### Configuration

Copy `.env.example` to `.env.local` and adjust as needed:
```bash
cp .env.example .env.local
```

By default the frontend connects to `/api`. Set `VITE_API_BASE_URL` in `.env.local` to override.

### Install
```bash
cd FinanceApp.Frontend
npm install
```

### Run
```bash
npm run dev
```

The app will be available at `http://localhost:3000`

### Build
```bash
npm run build
```

### Help Center maintenance note

Any PR that changes user-visible behavior, calculations, data interpretation, routes, workflows, warnings, or known limitations must update the relevant in-app Help Center article (`FinanceApp.Frontend/src/help`) and its focused tests in the same PR.

---

## Experimental: finanzen.net Pre-Market Quote Provider

> **⚠️ Experimental — disabled by default. For development research only. Not for production use.**

The `FinanzenNetQuoteService` is an optional, disabled-by-default enrichment provider that attempts to retrieve explicitly labeled pre-market prices from [finanzen.net](https://www.finanzen.net) stock pages. It is not a replacement for the primary Yahoo Finance / Finnhub providers.

**Key characteristics:**
- Disabled by default (`Enabled: false`) — this setting must remain `false` in all tracked configuration files
- Only provides a price when the page **explicitly** labels the value as pre-market ("Vorbörslich" / "Pre-Market")
- Session labels are **never inferred** from clock time or market schedule
- All failures (timeout, changed markup, ambiguous data, HTTP errors) fall back silently to the Yahoo/Finnhub result
- Per-instrument `FinanzenNetSlug` field maps a stock to its finanzen.net page path
- Process-level request throttling and in-memory caching prevent excessive requests

### Legal and Robots.txt Notice

**Before enabling this provider, you must:**

1. Review `https://www.finanzen.net/robots.txt` at the time of use and verify that automated access to `/aktien/` paths is not disallowed for your User-Agent.
2. Review finanzen.net's Terms of Service regarding automated data access.
3. Ensure you are in compliance with all applicable usage restrictions.

> If automated access to the relevant path is disallowed or restricted, keep `Enabled: false` and do not use this feature. The service is designed so it can remain safely disabled indefinitely; the rest of the application is not affected.

**Disable immediately** if:
- You receive 403/429 responses
- finanzen.net's markup changes and parsing breaks
- You are uncertain about compliance

### Configuration

To enable locally (never in tracked `appsettings.json`), use user secrets or `appsettings.Development.json`:

```json
{
  "FinanzenNet": {
    "Enabled": true,
    "BaseUrl": "https://www.finanzen.net",
    "CacheDuration": "00:02:00",
    "MinRequestInterval": "00:00:05",
    "RequestTimeout": "00:00:15",
    "UserAgent": "FinanceApp-Dev/1.0 (development research tool)"
  }
}
```

| Option | Default | Description |
|---|---|---|
| `Enabled` | `false` | Must be `true` to activate. Never `true` in production config. |
| `BaseUrl` | `https://www.finanzen.net` | Base URL for the site. |
| `CacheDuration` | `00:05:00` | How long to cache a successfully parsed pre-market quote. |
| `MinRequestInterval` | `00:00:05` | Minimum delay between outgoing HTTP requests (process-level throttle). |
| `RequestTimeout` | `00:00:15` | Per-request HTTP timeout. |
| `UserAgent` | *(default dev string)* | User-Agent header sent with requests. |

### How to Configure a Slug

A finanzen.net instrument slug is the path segment after `/aktien/` in the stock's URL.
For example, `https://www.finanzen.net/aktien/microsoft-aktie` → slug is `microsoft-aktie`.

Add the slug to a stock via the **Stocks** form in the UI (field: **finanzen.net Slug**), or directly in the database. Valid slugs contain only lowercase letters, digits, hyphens, and underscores, and must start with a letter or digit. For example, `https://www.finanzen.net/aktien/western_digital-aktie` → slug is `western_digital-aktie`.

The `finanzenNetSlug` field is nullable. Stocks without a slug are unaffected by this provider.

### API Usage

The `GET /api/stockprice/{symbol}` endpoint accepts an optional `finanzenNetSlug` query parameter:

```
GET /api/stockprice/MSFT?exchange=NYSE&finanzenNetSlug=microsoft-aktie
```

When the provider is enabled and a valid pre-market price is found, the response includes:
- `priceSession: "PRE"` — explicitly labeled pre-market session
- `priceSource: "finanzen.net"` — auditable attribution
- `priceTimestampUtc` — provider timestamp (null if not supplied by the page)

When disabled, or when no pre-market price is found, the response reflects the normal Yahoo/Finnhub quote.

### Rate and Caching Behavior

- Successful pre-market results are cached in-memory for `CacheDuration` (default: 5 minutes).
- A process-level semaphore enforces `MinRequestInterval` (default: 5 seconds) between HTTP requests, preventing request bursts when the UI refreshes multiple stocks.
- Cache keys are per-slug.

### Markup Stability Caveat

finanzen.net's page structure may change without notice. If the parser cannot find an **unambiguous, explicitly labeled** pre-market section, it returns failure and the normal provider is used. This is intentional. Monitor logs for `FinanzenNet request failed` or `No explicitly labeled pre-market price found` messages.
