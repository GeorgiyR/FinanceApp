import type { StockQuoteResponse } from '../types';
import { isQuoteDelayed } from './quote';

type PersistedQuoteSnapshot = {
  currentPrice?: number | null;
  currentPriceChange?: number | null;
  currentPriceChangePercent?: number | null;
  currentPriceAt?: string | null;
  currentPriceIsDelayed?: boolean;
  currentPriceDelayWarning?: string | null;
};

export type CurrentPriceSnapshotSource = 'persisted' | 'live' | 'none';

export interface CurrentPriceSnapshot {
  source: CurrentPriceSnapshotSource;
  currentPrice: number | null;
  currentPriceChange: number | null;
  currentPriceChangePercent: number | null;
  currentPriceAt: string | null;
  isDelayed: boolean;
  delayWarning: string | null;
  liveQuote: StockQuoteResponse | null;
}

type Candidate = {
  source: Exclude<CurrentPriceSnapshotSource, 'none'>;
  timestampMs: number;
  snapshot: CurrentPriceSnapshot;
};

const isFiniteNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

const parseUtcTimestamp = (value: string | null | undefined): number => {
  if (!value) return Number.NaN;
  const normalized = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value) ? value : `${value}Z`;
  return Date.parse(normalized);
};

const emptySnapshot: CurrentPriceSnapshot = {
  source: 'none',
  currentPrice: null,
  currentPriceChange: null,
  currentPriceChangePercent: null,
  currentPriceAt: null,
  isDelayed: false,
  delayWarning: null,
  liveQuote: null,
};

const withCandidateTimestamp = (
  source: Exclude<CurrentPriceSnapshotSource, 'none'>,
  snapshot: CurrentPriceSnapshot,
  now: number,
): Candidate | null => {
  const timestampMs = parseUtcTimestamp(snapshot.currentPriceAt);
  if (!Number.isFinite(timestampMs) || timestampMs > now) {
    return null;
  }

  return { source, timestampMs, snapshot };
};

/**
 * Selects the current-price snapshot by timestamp, not by fixed source priority.
 *
 * Rules:
 * 1. Compare only valid timestamps (parseable UTC and not in the future).
 * 2. Newer valid timestamp wins.
 * 3. Equal timestamps: prefer a non-delayed snapshot over a delayed one.
 * 4. Otherwise, prefer persisted snapshot (safe deterministic precedence).
 * 5. If no valid timestamps exist, fall back deterministically to persisted first,
 *    then live, then empty snapshot.
 */
export const resolveNewestCurrentPriceSnapshot = (
  persisted: PersistedQuoteSnapshot,
  liveQuote: StockQuoteResponse | null | undefined,
  now: number = Date.now(),
): CurrentPriceSnapshot => {
  const persistedSnapshot: CurrentPriceSnapshot = {
    source: 'persisted',
    currentPrice: isFiniteNumber(persisted.currentPrice) ? persisted.currentPrice : null,
    currentPriceChange: isFiniteNumber(persisted.currentPriceChange) ? persisted.currentPriceChange : null,
    currentPriceChangePercent: isFiniteNumber(persisted.currentPriceChangePercent)
      ? persisted.currentPriceChangePercent
      : null,
    currentPriceAt: persisted.currentPriceAt ?? null,
    isDelayed: persisted.currentPriceIsDelayed === true || !!persisted.currentPriceDelayWarning,
    delayWarning: persisted.currentPriceDelayWarning?.trim() || null,
    liveQuote: null,
  };
  const liveCurrentPriceEur = liveQuote?.currentPriceEur;
  const liveChangeEur = liveQuote?.changeEur;
  const livePercentChange = liveQuote?.percentChange;

  const liveSnapshot: CurrentPriceSnapshot = {
    source: 'live',
    currentPrice: isFiniteNumber(liveCurrentPriceEur) ? liveCurrentPriceEur : null,
    currentPriceChange: isFiniteNumber(liveChangeEur) ? liveChangeEur : null,
    currentPriceChangePercent: isFiniteNumber(livePercentChange) ? livePercentChange : null,
    currentPriceAt: liveQuote?.priceTimestampUtc ?? null,
    isDelayed: isQuoteDelayed(liveQuote),
    delayWarning: liveQuote?.delayWarning ?? null,
    liveQuote: liveQuote ?? null,
  };

  const candidates: Candidate[] = [];
  const persistedCandidate = withCandidateTimestamp('persisted', persistedSnapshot, now);
  if (persistedCandidate && persistedSnapshot.currentPrice != null) {
    candidates.push(persistedCandidate);
  }
  const liveCandidate = withCandidateTimestamp('live', liveSnapshot, now);
  if (liveCandidate && liveSnapshot.currentPrice != null) {
    candidates.push(liveCandidate);
  }

  if (candidates.length > 0) {
    const selected = candidates.reduce((best, current) => {
      if (current.timestampMs > best.timestampMs) return current;
      if (current.timestampMs < best.timestampMs) return best;
      if (current.snapshot.isDelayed !== best.snapshot.isDelayed) {
        return current.snapshot.isDelayed ? best : current;
      }
      if (best.source === 'persisted') return best;
      if (current.source === 'persisted') return current;
      return best;
    });
    return selected.snapshot;
  }

  if (persistedSnapshot.currentPrice != null) {
    return persistedSnapshot;
  }
  if (liveSnapshot.currentPrice != null) {
    return liveSnapshot;
  }
  return emptySnapshot;
};

export const shouldKeepLiveOverlayAfterPersistedRefresh = (
  persisted: PersistedQuoteSnapshot,
  liveQuote: StockQuoteResponse | null | undefined,
  now: number = Date.now(),
): boolean => {
  const persistedTs = parseUtcTimestamp(persisted.currentPriceAt ?? null);
  const liveTs = parseUtcTimestamp(liveQuote?.priceTimestampUtc ?? null);
  const persistedHasPrice = isFiniteNumber(persisted.currentPrice);
  const liveHasPrice = isFiniteNumber(liveQuote?.currentPriceEur);

  if (!liveHasPrice || !Number.isFinite(liveTs) || liveTs > now) {
    return false;
  }

  if (!persistedHasPrice || !Number.isFinite(persistedTs) || persistedTs > now) {
    return true;
  }

  if (liveTs > persistedTs) {
    return true;
  }

  if (liveTs < persistedTs) {
    return false;
  }

  const persistedDelayed =
    persisted.currentPriceIsDelayed === true || !!persisted.currentPriceDelayWarning;
  const liveDelayed = isQuoteDelayed(liveQuote);

  if (liveDelayed !== persistedDelayed) {
    return !liveDelayed;
  }

  return false;
};
