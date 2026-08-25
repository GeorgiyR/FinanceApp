import { describe, expect, it } from 'vitest';
import {
  MARKET_INDICES_SIDEBAR_PARENT_KEY,
  SYSTEM_PARENT_KEY,
  SYSTEM_PROCESS_JOURNAL_KEY,
  applySidebarOpenChange,
  computeSidebarOpenKeys,
  marketIndexSidebarKey,
} from './AppSidebar';

describe('AppSidebar open state', () => {
  it('keeps /stocks opening only the stocks submenu', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: ['stocks-list'],
    });

    expect(keys).toContain('stocks');
    expect(keys).not.toContain('stocks-directories');
    expect(keys).not.toContain('market-indices-root');
  });

  it('keeps /stocks/catalog opening only the stocks submenu', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: ['stocks-catalog'],
    });

    expect(keys).toContain('stocks');
    expect(keys).not.toContain('stocks-directories');
    expect(keys).not.toContain('market-indices-root');
  });

  it('opens stocks (but not market-indices-root) on an index route when marketIndicesOpen is false', () => {
    // Route-based initial reveal is handled by the useState initializer, not computeSidebarOpenKeys.
    // When marketIndicesOpen=false (explicit close or persisted preference), route does NOT force it open.
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: [marketIndexSidebarKey(5)],
    });

    expect(keys).toContain('stocks');
    expect(keys).not.toContain('market-indices-root');
  });

  it('opens stocks and nested market indices on an index route when marketIndicesOpen is true', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: true,
      selectedKeys: [marketIndexSidebarKey(5)],
    });

    expect(keys).toEqual(['stocks', 'market-indices-root']);
  });

  it('opens top-level directories independently for /sectors', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: ['sectors'],
    });

    expect(keys).toContain('stocks-directories');
    expect(keys).not.toContain('stocks');
    expect(keys).not.toContain('market-indices-root');
  });

  it('opens top-level directories independently for /help', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: ['help'],
    });

    expect(keys).toContain('stocks-directories');
    expect(keys).not.toContain('stocks');
    expect(keys).not.toContain('market-indices-root');
  });

  it('opens system section for process journal route', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      selectedKeys: [SYSTEM_PROCESS_JOURNAL_KEY],
    });

    expect(keys).toContain(SYSTEM_PARENT_KEY);
    expect(keys).not.toContain('stocks');
    expect(keys).not.toContain('stocks-directories');
  });

  it('route-required parents win over stale persisted preferences', () => {
    const keys = computeSidebarOpenKeys({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: true,
      selectedKeys: [marketIndexSidebarKey(7)],
    });

    expect(keys).toEqual(['stocks', 'market-indices-root']);
  });

  it('opening nested market indices also persists stocks as open', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: false,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      marketIndicesDescendantOpenKeys: [],
      selectedKeys: ['dashboard'],
      newOpenKeys: ['stocks', 'market-indices-root'],
    });

    expect(state.stocksOpen).toBe(true);
    expect(state.marketIndicesOpen).toBe(true);
  });

  it('closing stocks clears nested market indices when the route does not require them', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: true,
      marketIndicesDescendantOpenKeys: [`${MARKET_INDICES_SIDEBAR_PARENT_KEY}/advanced`],
      selectedKeys: ['dashboard'],
      newOpenKeys: [],
    });

    expect(state.stocksOpen).toBe(false);
    expect(state.marketIndicesOpen).toBe(false);
    expect(state.marketIndicesDescendantOpenKeys).toEqual([]);
  });

  it('closing directories on a non-directory route only updates the directories state', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: true,
      marketIndicesOpen: false,
      marketIndicesDescendantOpenKeys: [],
      selectedKeys: ['dashboard'],
      newOpenKeys: ['stocks'],
    });

    expect(state.stocksOpen).toBe(true);
    expect(state.stocksDirectoriesOpen).toBe(false);
    expect(state.marketIndicesOpen).toBe(false);
  });

  it('explicit close on an active index route is honoured (route does not override user intent)', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: true,
      marketIndicesDescendantOpenKeys: [],
      selectedKeys: [marketIndexSidebarKey(3)],
      newOpenKeys: ['stocks'],
    });

    expect(state.marketIndicesOpen).toBe(false);
    expect(state.marketIndicesDescendantOpenKeys).toEqual([]);
    expect(state.stocksOpen).toBe(true);
  });

  it('closing market indices from onOpenChange while on tracked stocks route closes it (leaf navigation does not call onOpenChange)', () => {
    // onOpenChange is NOT called on leaf clicks, so this represents a genuine user
    // submenu close action: market indices should be closed and descendants cleared.
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: true,
      marketIndicesDescendantOpenKeys: [`${MARKET_INDICES_SIDEBAR_PARENT_KEY}/advanced`],
      selectedKeys: ['stocks-list'],
      newOpenKeys: ['stocks'],
    });

    expect(state.stocksOpen).toBe(true);
    expect(state.marketIndicesOpen).toBe(false);
    expect(state.marketIndicesDescendantOpenKeys).toEqual([]);
  });

  it('keeps market indices closed when tracked stocks navigation starts from closed state', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      marketIndicesDescendantOpenKeys: [],
      selectedKeys: ['stocks-list'],
      newOpenKeys: ['stocks'],
    });

    expect(state.stocksOpen).toBe(true);
    expect(state.marketIndicesOpen).toBe(false);
    expect(state.marketIndicesDescendantOpenKeys).toEqual([]);
  });

  it('onOpenChange toggles market indices open then closed while keeping stocks open', () => {
    const opened = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: false,
      marketIndicesOpen: false,
      marketIndicesDescendantOpenKeys: [],
      selectedKeys: ['dashboard'],
      newOpenKeys: ['stocks', 'market-indices-root'],
    });

    const closed = applySidebarOpenChange({
      ...opened,
      selectedKeys: ['dashboard'],
      newOpenKeys: ['stocks'],
    });

    expect(opened.marketIndicesOpen).toBe(true);
    expect(closed.marketIndicesOpen).toBe(false);
    expect(closed.stocksOpen).toBe(true);
  });

  it('market indices close clears descendants and keeps unrelated sections unchanged', () => {
    const state = applySidebarOpenChange({
      portfoliosOpen: false,
      stocksOpen: true,
      stocksDirectoriesOpen: true,
      marketIndicesOpen: true,
      marketIndicesDescendantOpenKeys: [
        `${MARKET_INDICES_SIDEBAR_PARENT_KEY}/advanced`,
        `${MARKET_INDICES_SIDEBAR_PARENT_KEY}-nested`,
      ],
      selectedKeys: ['dashboard'],
      newOpenKeys: ['stocks', 'stocks-directories'],
    });

    expect(state.stocksOpen).toBe(true);
    expect(state.stocksDirectoriesOpen).toBe(true);
    expect(state.marketIndicesOpen).toBe(false);
    expect(state.marketIndicesDescendantOpenKeys).toEqual([]);
  });
});
