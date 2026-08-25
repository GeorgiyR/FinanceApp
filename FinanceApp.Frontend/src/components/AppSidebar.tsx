import React, { useState, useMemo, useCallback } from 'react';
import {
  ApartmentOutlined,
  BookOutlined,
  CalculatorOutlined,
  DashboardOutlined,
  FolderOutlined,
  FolderOpenOutlined,
  GlobalOutlined,
  LogoutOutlined,
  QuestionCircleOutlined,
  SettingOutlined,
  StockOutlined,
  UnorderedListOutlined,
  UserOutlined,
  WalletOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
} from '@ant-design/icons';
import { Button, Drawer, Grid, Layout, Menu, Tooltip } from 'antd';
import type { MenuProps } from 'antd';
import { useNavigate } from 'react-router-dom';
import type { MarketIndex, Portfolio } from '../types';
import './AppSidebar.css';

const { Sider } = Layout;
const { useBreakpoint } = Grid;
const PORTFOLIO_KEY_PREFIX = 'portfolio-';
export const PROFILE_ROUTE = '/profile';
const SIDEBAR_EXPANDED_WIDTH = 270;
const SIDEBAR_COLLAPSED_WIDTH = 64;
const SIDEBAR_COLLAPSED_STORAGE_KEY = 'financeapp.sidebar.collapsed';
const PORTFOLIOS_OPEN_STORAGE_KEY = 'financeapp.sidebar.portfolios.open';
const STOCKS_OPEN_STORAGE_KEY = 'financeapp.sidebar.stocks.open';
const STOCKS_DIRECTORIES_OPEN_STORAGE_KEY = 'financeapp.sidebar.stocks-directories.open';
const SYSTEM_OPEN_STORAGE_KEY = 'financeapp.sidebar.system.open';
const MARKET_INDICES_OPEN_STORAGE_KEY = 'financeapp.sidebar.market-indices.open';
const MARKET_INDICES_DESCENDANT_OPEN_KEYS_STORAGE_KEY = 'financeapp.sidebar.market-indices.descendant-open-keys';

export const MARKET_INDICES_SIDEBAR_PARENT_KEY = 'market-indices-root';
export const MARKET_INDEX_KEY_PREFIX = 'market-index-';
export const MARKET_INDICES_MANAGE_KEY = 'market-indices-manage';
export const STOCKS_CATALOG_KEY = 'stocks-catalog';
export const SYSTEM_PARENT_KEY = 'system';
export const SYSTEM_PROCESS_JOURNAL_KEY = 'system-processes';

export function marketIndexSidebarKey(id: number): string {
  return `${MARKET_INDEX_KEY_PREFIX}${id}`;
}

export function marketIndexRoute(id: number): string {
  return `/market-indices/${id}`;
}

export interface SidebarOpenState {
  portfoliosOpen: boolean;
  stocksOpen: boolean;
  stocksDirectoriesOpen: boolean;
  systemOpen: boolean;
  marketIndicesOpen: boolean;
  marketIndicesDescendantOpenKeys: string[];
}

export interface SidebarOpenKeysParams extends Omit<SidebarOpenState, 'marketIndicesDescendantOpenKeys' | 'systemOpen'> {
  systemOpen?: boolean;
  selectedKeys: string[];
  activePortfolioId?: string | number;
  defaultOpenKeys?: string[];
  marketIndicesDescendantOpenKeys?: string[];
}

export interface SidebarOpenChangeParams extends SidebarOpenKeysParams {
  newOpenKeys: string[];
}

export type PortfolioSection = 'positions' | 'transactions';

export interface AppSidebarProps {
  portfolios: Portfolio[];
  selectedKeys: string[];
  userName?: string;
  onLogout: () => void;
  defaultOpenKeys?: string[];
  /** ID of the currently viewed portfolio, if any */
  activePortfolioId?: string | number;
  /** Market indices for the nested dynamic submenu under Stocks. Pass an empty array while loading. */
  marketIndices?: MarketIndex[];
}

export interface StocksDirectoriesMenuEntry {
  key: 'sectors' | 'financial-metrics' | 'help';
  label: string;
  route: string;
  icon: React.ReactElement;
}

export const STOCKS_DIRECTORIES_PARENT_KEY = 'stocks-directories';
export const STOCKS_DIRECTORIES_PARENT_LABEL = 'Справочники';
export const STOCKS_DIRECTORIES_PARENT_ICON = <BookOutlined />;

export const STOCKS_DIRECTORIES_MENU_ENTRIES: StocksDirectoriesMenuEntry[] = [
  {
    key: 'sectors',
    label: 'Секторы и отрасли',
    route: '/sectors',
    icon: <ApartmentOutlined />,
  },
  {
    key: 'financial-metrics',
    label: 'Финансовые показатели',
    route: '/financial-metrics',
    icon: <CalculatorOutlined />,
  },
  {
    key: 'help',
    label: 'Справка',
    route: '/help',
    icon: <QuestionCircleOutlined />,
  },
];

export function buildStocksDirectoriesMenuItems(
  onNavigate: (route: string) => void,
): NonNullable<MenuProps['items']> {
  return STOCKS_DIRECTORIES_MENU_ENTRIES.map((entry) => ({
    key: entry.key,
    icon: entry.icon,
    label: entry.label,
    onClick: () => onNavigate(entry.route),
  }));
}

export function isStocksDirectoriesSelectedKey(key: string): boolean {
  return key === STOCKS_DIRECTORIES_PARENT_KEY
    || key === 'sectors'
    || key === 'financial-metrics'
    || key === 'help'
    || key.startsWith('sectors-');
}

export function isSystemSelectedKey(key: string): boolean {
  return key === SYSTEM_PARENT_KEY || key === SYSTEM_PROCESS_JOURNAL_KEY;
}

export function isMarketIndicesSelectedKey(key: string): boolean {
  return key === MARKET_INDICES_SIDEBAR_PARENT_KEY
    || key === MARKET_INDICES_MANAGE_KEY
    || key.startsWith(MARKET_INDEX_KEY_PREFIX);
}

function isMarketIndicesDescendantOpenKey(key: string): boolean {
  return key.startsWith(`${MARKET_INDICES_SIDEBAR_PARENT_KEY}-`)
    || key.startsWith(`${MARKET_INDICES_SIDEBAR_PARENT_KEY}/`)
    || key.startsWith(MARKET_INDEX_KEY_PREFIX);
}

function filterMarketIndicesDescendantOpenKeys(keys: string[]): string[] {
  return Array.from(new Set(keys.filter(isMarketIndicesDescendantOpenKey)));
}

export function isStocksSelectedKey(key: string): boolean {
  return key === 'stocks'
    || key === 'stocks-list'
    || key === STOCKS_CATALOG_KEY
    || isMarketIndicesSelectedKey(key);
}

export function computeSidebarOpenKeys({
  portfoliosOpen,
  stocksOpen,
  stocksDirectoriesOpen,
  systemOpen = false,
  marketIndicesOpen,
  marketIndicesDescendantOpenKeys = [],
  selectedKeys,
  activePortfolioId,
  defaultOpenKeys,
}: SidebarOpenKeysParams): string[] {
  const keys: string[] = [];
  const hasStocksSelection = selectedKeys.some(isStocksSelectedKey);
  const hasStocksDirectoriesSelection = selectedKeys.some(isStocksDirectoriesSelectedKey);
  const hasSystemSelection = selectedKeys.some(isSystemSelectedKey);
  const marketIndicesDescendantKeys = filterMarketIndicesDescendantOpenKeys(marketIndicesDescendantOpenKeys);
  const stocksRootOpen = stocksOpen || hasStocksSelection;
  // marketIndicesSubtreeOpen deliberately uses only the controlled state, NOT selectedKeys.
  // Route-based initial reveal is handled by the useState initializer in AppSidebar.
  // This ensures an explicit user close is always honoured even on active index routes.
  const marketIndicesSubtreeOpen = marketIndicesOpen;

  if (portfoliosOpen || activePortfolioId != null) {
    keys.push('portfolios');
  }
  if (stocksRootOpen || marketIndicesSubtreeOpen) {
    keys.push('stocks');
  }
  if (stocksDirectoriesOpen || hasStocksDirectoriesSelection) {
    keys.push(STOCKS_DIRECTORIES_PARENT_KEY);
  }
  if (systemOpen || hasSystemSelection) {
    keys.push(SYSTEM_PARENT_KEY);
  }
  if (marketIndicesSubtreeOpen) {
    keys.push(MARKET_INDICES_SIDEBAR_PARENT_KEY);
    for (const key of marketIndicesDescendantKeys) {
      if (!keys.includes(key)) keys.push(key);
    }
  }
  if (activePortfolioId != null) {
    keys.push(`${PORTFOLIO_KEY_PREFIX}${activePortfolioId}`);
  }
  if (defaultOpenKeys) {
    for (const key of defaultOpenKeys) {
      if (!marketIndicesSubtreeOpen && isMarketIndicesDescendantOpenKey(key)) {
        continue;
      }
      if (!keys.includes(key)) keys.push(key);
    }
  }

  return keys;
}

export function applySidebarOpenChange({
  portfoliosOpen,
  stocksOpen,
  stocksDirectoriesOpen,
  systemOpen = false,
  marketIndicesOpen,
  marketIndicesDescendantOpenKeys = [],
  selectedKeys,
  activePortfolioId,
  defaultOpenKeys,
  newOpenKeys,
}: SidebarOpenChangeParams): SidebarOpenState {
  // onOpenChange is only fired on submenu open/close, not on leaf item clicks.
  // We can therefore trust the diff between current and new keys as the true user intent.
  const currentMarketIndicesDescendantKeys = filterMarketIndicesDescendantOpenKeys(marketIndicesDescendantOpenKeys);
  const nextMarketIndicesDescendantKeys = filterMarketIndicesDescendantOpenKeys(newOpenKeys);
  const currentKeys = computeSidebarOpenKeys({
    portfoliosOpen,
    stocksOpen,
    stocksDirectoriesOpen,
    systemOpen,
    marketIndicesOpen,
    marketIndicesDescendantOpenKeys: currentMarketIndicesDescendantKeys,
    selectedKeys,
    activePortfolioId,
    defaultOpenKeys,
  });

  let nextPortfoliosOpen = portfoliosOpen;
  let nextStocksOpen = stocksOpen;
  let nextStocksDirectoriesOpen = stocksDirectoriesOpen;
  let nextSystemOpen = systemOpen;
  let nextMarketIndicesOpen = marketIndicesOpen;
  let nextMarketIndicesDescendantOpenKeys = currentMarketIndicesDescendantKeys;

  const prevHasPortfolios = currentKeys.includes('portfolios');
  const nextHasPortfolios = newOpenKeys.includes('portfolios');
  if (prevHasPortfolios && !nextHasPortfolios) {
    if (activePortfolioId == null) {
      nextPortfoliosOpen = false;
    }
  } else if (!prevHasPortfolios && nextHasPortfolios) {
    nextPortfoliosOpen = true;
  }

  const prevHasStocks = currentKeys.includes('stocks');
  const nextHasStocks = newOpenKeys.includes('stocks');
  const routeRequiresStocks = selectedKeys.some(isStocksSelectedKey);
  if (prevHasStocks && !nextHasStocks) {
    if (!routeRequiresStocks) {
      nextStocksOpen = false;
      nextMarketIndicesOpen = false;
      nextMarketIndicesDescendantOpenKeys = [];
    }
  } else if (!prevHasStocks && nextHasStocks) {
    nextStocksOpen = true;
  }

  const prevHasStocksDirectories = currentKeys.includes(STOCKS_DIRECTORIES_PARENT_KEY);
  const nextHasStocksDirectories = newOpenKeys.includes(STOCKS_DIRECTORIES_PARENT_KEY);
  const routeRequiresDirectories = selectedKeys.some(isStocksDirectoriesSelectedKey);
  if (prevHasStocksDirectories && !nextHasStocksDirectories) {
    if (!routeRequiresDirectories) {
      nextStocksDirectoriesOpen = false;
    }
  } else if (!prevHasStocksDirectories && nextHasStocksDirectories) {
    nextStocksDirectoriesOpen = true;
  }

  const prevHasSystem = currentKeys.includes(SYSTEM_PARENT_KEY);
  const nextHasSystem = newOpenKeys.includes(SYSTEM_PARENT_KEY);
  const routeRequiresSystem = selectedKeys.some(isSystemSelectedKey);
  if (prevHasSystem && !nextHasSystem) {
    if (!routeRequiresSystem) {
      nextSystemOpen = false;
    }
  } else if (!prevHasSystem && nextHasSystem) {
    nextSystemOpen = true;
  }

  // Market Indices: onOpenChange is the single authority.
  // Because onOpenChange is never fired for leaf clicks, any diff here is a genuine
  // submenu open or close action by the user (or a cascade from closing stocks).
  // An explicit close is always honoured — even when the active route is inside
  // the market-indices subtree — so that a repeated click on the title actually
  // collapses the submenu. Initial reveal is handled by the useState initializer
  // (one-time, not re-evaluated on every render).
  const prevHasMarketIndices = currentKeys.includes(MARKET_INDICES_SIDEBAR_PARENT_KEY);
  const nextHasMarketIndices = newOpenKeys.includes(MARKET_INDICES_SIDEBAR_PARENT_KEY);
  if (prevHasMarketIndices && !nextHasMarketIndices) {
    // User explicitly closed the submenu (or stocks cascade closed it).
    nextMarketIndicesOpen = false;
    nextMarketIndicesDescendantOpenKeys = [];
  } else if (!prevHasMarketIndices && nextHasMarketIndices) {
    nextMarketIndicesOpen = true;
    nextStocksOpen = true;
    nextMarketIndicesDescendantOpenKeys = nextMarketIndicesDescendantKeys;
  } else if (nextHasMarketIndices) {
    nextMarketIndicesDescendantOpenKeys = nextMarketIndicesDescendantKeys;
  } else if (!nextMarketIndicesOpen) {
    nextMarketIndicesDescendantOpenKeys = [];
  }

  return {
    portfoliosOpen: nextPortfoliosOpen,
    stocksOpen: nextStocksOpen,
    stocksDirectoriesOpen: nextStocksDirectoriesOpen,
    systemOpen: nextSystemOpen,
    marketIndicesOpen: nextMarketIndicesOpen,
    marketIndicesDescendantOpenKeys: nextMarketIndicesDescendantOpenKeys,
  };
}

export interface BuildSidebarMenuItemsParams {
  portfolios: Portfolio[];
  activePortfolioId?: string | number;
  marketIndices?: MarketIndex[];
  onNavigate: (route: string) => void;
}

export function buildSidebarMenuItems({
  portfolios,
  activePortfolioId,
  marketIndices = [],
  onNavigate,
}: BuildSidebarMenuItemsParams): MenuProps['items'] {
  const buildPortfolioChildren = (portfolio: Portfolio): NonNullable<MenuProps['items']> => {
    const pid = portfolio.id;
    return [
      {
        key: `${PORTFOLIO_KEY_PREFIX}${pid}-positions`,
        className: 'sidebar-leaf-item',
        icon: <UnorderedListOutlined />,
        label: 'Позиции',
        onClick: () => onNavigate(`/portfolios/${pid}?section=positions`),
      },
      {
        key: `${PORTFOLIO_KEY_PREFIX}${pid}-transactions`,
        className: 'sidebar-leaf-item',
        icon: <WalletOutlined />,
        label: 'Транзакции',
        onClick: () => onNavigate(`/portfolios/${pid}?section=transactions`),
      },
    ];
  };

  const portfolioChildren: NonNullable<MenuProps['items']> = portfolios
    .map((portfolio) => {
      const pid = portfolio.id;
      const isActive = activePortfolioId != null && String(portfolio.id) === String(activePortfolioId);
      const children = buildPortfolioChildren(portfolio);

      if (isActive) {
        return {
          key: `${PORTFOLIO_KEY_PREFIX}${pid}`,
          className: 'sidebar-portfolio-node sidebar-portfolio-node--active',
          icon: <FolderOpenOutlined />,
          label: (
            <Tooltip title={portfolio.name} placement="right">
              <span className="sidebar-node-label">{portfolio.name}</span>
            </Tooltip>
          ),
          children,
        };
      }
      return {
        key: `${PORTFOLIO_KEY_PREFIX}${pid}`,
        className: 'sidebar-portfolio-node',
        icon: <FolderOutlined />,
        label: (
          <Tooltip title={portfolio.name} placement="right">
            <span className="sidebar-node-label">{portfolio.name}</span>
          </Tooltip>
        ),
        onClick: () => onNavigate(`/portfolios/${pid}`),
        children: undefined,
      };
    });

  const marketIndexChildren: NonNullable<MenuProps['items']> = marketIndices
    .filter((idx) => !idx.isArchived && idx.showInNavigation !== false)
    .map((idx) => {
      const tooltip = idx.name === idx.code ? idx.name : `${idx.name} (${idx.code})`;
      return {
        key: marketIndexSidebarKey(idx.id),
        className: 'sidebar-leaf-item',
        icon: <GlobalOutlined />,
        label: (
          <Tooltip title={tooltip} placement="right">
            <span className="sidebar-node-label">{idx.name}</span>
          </Tooltip>
        ),
        onClick: () => onNavigate(marketIndexRoute(idx.id)),
      };
    });

  return [
    {
      key: 'dashboard',
      icon: <DashboardOutlined />,
      label: 'Главная',
      onClick: () => onNavigate('/'),
    },
    {
      key: 'portfolios',
      icon: <FolderOutlined />,
      label: 'Портфели',
      children: portfolioChildren,
    },
    {
      key: 'stocks',
      icon: <StockOutlined />,
      label: 'Акции',
      children: [
        {
          key: 'stocks-list',
          icon: <UnorderedListOutlined />,
          label: 'Отслеживаемые акции',
          onClick: () => onNavigate('/stocks'),
        },
        {
          key: STOCKS_CATALOG_KEY,
          icon: <UnorderedListOutlined />,
          label: 'Список акций',
          onClick: () => onNavigate('/stocks/catalog'),
        },
        {
          key: MARKET_INDICES_SIDEBAR_PARENT_KEY,
          icon: <GlobalOutlined />,
          label: 'Мировые индексы',
          children: [
            {
              key: MARKET_INDICES_MANAGE_KEY,
              className: 'sidebar-leaf-item',
              icon: <UnorderedListOutlined />,
              label: 'Управление',
              onClick: () => onNavigate('/market-indices'),
            },
            ...marketIndexChildren,
          ],
        },
      ],
    },
    {
      key: STOCKS_DIRECTORIES_PARENT_KEY,
      icon: STOCKS_DIRECTORIES_PARENT_ICON,
      label: STOCKS_DIRECTORIES_PARENT_LABEL,
      children: buildStocksDirectoriesMenuItems(onNavigate),
    },
    {
      key: SYSTEM_PARENT_KEY,
      icon: <SettingOutlined />,
      label: 'Система',
      children: [
        {
          key: SYSTEM_PROCESS_JOURNAL_KEY,
          icon: <UnorderedListOutlined />,
          label: 'Журнал процессов',
          onClick: () => onNavigate('/system/processes'),
        },
      ],
    },
  ];
}

const AppSidebar: React.FC<AppSidebarProps> = ({
  portfolios,
  selectedKeys,
  userName,
  onLogout,
  defaultOpenKeys,
  activePortfolioId,
  marketIndices = [],
}) => {
  const navigate = useNavigate();
  const screens = useBreakpoint();
  const isMobile = !screens.md;
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    if (typeof window === 'undefined') return false;
    try {
      return window.localStorage.getItem(SIDEBAR_COLLAPSED_STORAGE_KEY) === '1';
    } catch {
      return false;
    }
  });
  const [mobileOpen, setMobileOpen] = useState(false);
  const isSidebarCollapsed = !isMobile && collapsed;

  // Controlled portfolios open state: persisted in localStorage.
  // On first load, default to open when on a portfolio route or when explicitly set.
  const [portfoliosOpen, setPortfoliosOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') {
      return selectedKeys.some((k) => k.startsWith(PORTFOLIO_KEY_PREFIX));
    }
    try {
      const stored = window.localStorage.getItem(PORTFOLIOS_OPEN_STORAGE_KEY);
      if (stored !== null) return stored === '1';
    } catch {}
    // Fall back to route-based detection for first-ever visit
    return selectedKeys.some((k) => k.startsWith(PORTFOLIO_KEY_PREFIX));
  });

  const [stocksOpen, setStocksOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') {
      return selectedKeys.some(isStocksSelectedKey);
    }
    try {
      const stored = window.localStorage.getItem(STOCKS_OPEN_STORAGE_KEY);
      if (stored !== null) return stored === '1';
    } catch {}
    return selectedKeys.some(isStocksSelectedKey);
  });

  const [stocksDirectoriesOpen, setStocksDirectoriesOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') {
      return selectedKeys.some(isStocksDirectoriesSelectedKey);
    }
    try {
      const stored = window.localStorage.getItem(STOCKS_DIRECTORIES_OPEN_STORAGE_KEY);
      if (stored !== null) return stored === '1';
    } catch {}
    return selectedKeys.some(isStocksDirectoriesSelectedKey);
  });
  const [systemOpen, setSystemOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') {
      return selectedKeys.some(isSystemSelectedKey);
    }
    try {
      const stored = window.localStorage.getItem(SYSTEM_OPEN_STORAGE_KEY);
      if (stored !== null) return stored === '1';
    } catch {}
    return selectedKeys.some(isSystemSelectedKey);
  });

  const [marketIndicesOpen, setMarketIndicesOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') {
      return selectedKeys.some(isMarketIndicesSelectedKey);
    }
    try {
      const stored = window.localStorage.getItem(MARKET_INDICES_OPEN_STORAGE_KEY);
      if (stored !== null) return stored === '1';
    } catch {}
    return selectedKeys.some(isMarketIndicesSelectedKey);
  });
  const [marketIndicesDescendantOpenKeys, setMarketIndicesDescendantOpenKeys] = useState<string[]>(() => {
    if (typeof window === 'undefined') {
      return [];
    }
    try {
      const stored = window.localStorage.getItem(MARKET_INDICES_DESCENDANT_OPEN_KEYS_STORAGE_KEY);
      if (!stored) return [];
      const parsed = JSON.parse(stored);
      return Array.isArray(parsed) ? filterMarketIndicesDescendantOpenKeys(parsed.filter((value): value is string => typeof value === 'string')) : [];
    } catch {
      return [];
    }
  });
  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(SIDEBAR_COLLAPSED_STORAGE_KEY, collapsed ? '1' : '0');
    } catch {}
  }, [collapsed]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(PORTFOLIOS_OPEN_STORAGE_KEY, portfoliosOpen ? '1' : '0');
    } catch {}
  }, [portfoliosOpen]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(STOCKS_OPEN_STORAGE_KEY, stocksOpen ? '1' : '0');
    } catch {}
  }, [stocksOpen]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(STOCKS_DIRECTORIES_OPEN_STORAGE_KEY, stocksDirectoriesOpen ? '1' : '0');
    } catch {}
  }, [stocksDirectoriesOpen]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(SYSTEM_OPEN_STORAGE_KEY, systemOpen ? '1' : '0');
    } catch {}
  }, [systemOpen]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(MARKET_INDICES_OPEN_STORAGE_KEY, marketIndicesOpen ? '1' : '0');
    } catch {}
  }, [marketIndicesOpen]);

  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(
        MARKET_INDICES_DESCENDANT_OPEN_KEYS_STORAGE_KEY,
        JSON.stringify(filterMarketIndicesDescendantOpenKeys(marketIndicesDescendantOpenKeys)),
      );
    } catch {}
  }, [marketIndicesDescendantOpenKeys]);

  const openKeys = useMemo((): string[] => {
    return computeSidebarOpenKeys({
      portfoliosOpen,
      stocksOpen,
      stocksDirectoriesOpen,
      systemOpen,
      marketIndicesOpen,
      marketIndicesDescendantOpenKeys,
      activePortfolioId,
      defaultOpenKeys,
      selectedKeys,
    });
  }, [portfoliosOpen, stocksOpen, stocksDirectoriesOpen, systemOpen, marketIndicesOpen, marketIndicesDescendantOpenKeys, activePortfolioId, defaultOpenKeys, selectedKeys]);

  // Handle all submenu open/close changes from Ant Design.
  // onOpenChange is the single authority for submenu state: it fires only on submenu
  // open/close actions, not on leaf item clicks, so any diff is genuine user intent.
  const handleMenuOpenChange = useCallback((newOpenKeys: string[]) => {
    const nextState = applySidebarOpenChange({
      portfoliosOpen,
      stocksOpen,
      stocksDirectoriesOpen,
      systemOpen,
      marketIndicesOpen,
      marketIndicesDescendantOpenKeys,
      activePortfolioId,
      defaultOpenKeys,
      selectedKeys,
      newOpenKeys,
    });

    setPortfoliosOpen(nextState.portfoliosOpen);
    setStocksOpen(nextState.stocksOpen);
    setStocksDirectoriesOpen(nextState.stocksDirectoriesOpen);
    setSystemOpen(nextState.systemOpen);
    setMarketIndicesOpen(nextState.marketIndicesOpen);
    setMarketIndicesDescendantOpenKeys(nextState.marketIndicesDescendantOpenKeys);
  }, [
    activePortfolioId,
    defaultOpenKeys,
    marketIndicesDescendantOpenKeys,
    marketIndicesOpen,
    portfoliosOpen,
    selectedKeys,
    stocksDirectoriesOpen,
    systemOpen,
    stocksOpen,
  ]);

  const handleNavigate = useCallback((route: string) => {
    navigate(route);
    setMobileOpen(false);
  }, [navigate]);

  const allMenuItems: MenuProps['items'] = useMemo(() => buildSidebarMenuItems({
    portfolios,
    activePortfolioId,
    marketIndices,
    onNavigate: handleNavigate,
  }), [portfolios, activePortfolioId, marketIndices, handleNavigate]);

  const bottomItems: NonNullable<MenuProps['items']> = [
    {
      key: 'profile',
      icon: <UserOutlined />,
      label: (
        <Tooltip title={userName ?? 'Профиль'} placement="right">
          <span className="sidebar-node-label">{userName ?? 'Профиль'}</span>
        </Tooltip>
      ),
      onClick: () => { navigate(PROFILE_ROUTE); setMobileOpen(false); },
    },
    {
      key: 'logout',
      icon: <LogoutOutlined />,
      label: 'Выйти',
      onClick: onLogout,
      className: 'sidebar-logout-item',
      danger: true,
    },
  ];

  const sidebarContent = (
    <div className="sidebar-inner">
      {/* Brand header */}
      <div className="sidebar-brand">
        <span className="sidebar-brand-icon">💹</span>
        {!isSidebarCollapsed && <span className="sidebar-brand-text">FinanceApp</span>}
      </div>

      {/* Main nav */}
      <div className="sidebar-nav-scroll">
        <Menu
          className="sidebar-menu"
          theme="dark"
          mode="inline"
          inlineIndent={14}
          openKeys={isSidebarCollapsed ? [] : openKeys}
          onOpenChange={handleMenuOpenChange}
          selectedKeys={selectedKeys}
          items={allMenuItems}
          inlineCollapsed={isSidebarCollapsed}
        />
      </div>

      {/* Bottom account area */}
      <div className="sidebar-bottom">
        <Menu
          className="sidebar-menu sidebar-menu--bottom"
          theme="dark"
          mode="inline"
          inlineIndent={14}
          selectedKeys={[]}
          items={bottomItems}
          inlineCollapsed={isSidebarCollapsed}
        />
      </div>
    </div>
  );

  return (
    <>
      {/* Desktop: Sider + external collapse button, wrapped together on the gray shell background */}
      <div
        className={`sidebar-desktop-wrap${isSidebarCollapsed ? ' sidebar-desktop-wrap--collapsed' : ''}`}
        aria-hidden={isMobile}
      >
        <Sider
          className="app-sidebar"
          width={SIDEBAR_EXPANDED_WIDTH}
          collapsedWidth={SIDEBAR_COLLAPSED_WIDTH}
          collapsed={isSidebarCollapsed}
          collapsible={false}
          breakpoint="md"
          onBreakpoint={(broken) => {
            if (broken) setMobileOpen(false);
          }}
        >
          {sidebarContent}
        </Sider>
        {/* External collapse/expand button – below the blue panel, on the gray background */}
        {!isMobile && (
          <Button
            type="default"
            className="sidebar-collapse-btn"
            icon={isSidebarCollapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
            onClick={() => setCollapsed((c) => !c)}
            aria-label={isSidebarCollapsed ? 'Развернуть панель' : 'Свернуть панель'}
          />
        )}
      </div>

      {/* Mobile hamburger – only visible on small screens */}
      <Button
        type="text"
        className="sidebar-mobile-toggle"
        icon={<MenuUnfoldOutlined />}
        onClick={() => setMobileOpen(true)}
        aria-label="Открыть навигацию"
      />

      {/* Mobile drawer */}
      <Drawer
        rootClassName="app-sidebar-drawer"
        open={mobileOpen}
        onClose={() => setMobileOpen(false)}
        placement="left"
        width={SIDEBAR_EXPANDED_WIDTH}
        title="FinanceApp"
        styles={{
          body: { padding: 0, background: '#073f86' },
          header: { background: '#073f86', borderBottom: '1px solid rgba(255, 255, 255, 0.16)' },
          content: { background: '#073f86' },
        }}
        aria-label="Мобильная навигация"
      >
        {sidebarContent}
      </Drawer>
    </>
  );
};

export default AppSidebar;
