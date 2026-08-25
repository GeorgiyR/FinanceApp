import axios from 'axios';
import type {
  User,
  Portfolio,
  Stock,
  Order,
  Transaction,
  Dividend,
  PortfolioBalance,
  LoginRequest,
  RegisterRequest,
  CreatePortfolioRequest,
  AddPortfolioItemRequest,
  CreateStockRequest,
  UpdateStockRequest,
  UpdateStockMetadataRequest,
  UpdateStockQuoteRequest,
  UpdateStockQuoteResponse,
  StockHistoryResponse,
  StockHistoryRefreshResponse,
  StockHistoryRepairDiagnosticsResponse,
  StockHistoryRange,
  StockQuoteResponse,
  StockExchange,
  CreateOrderRequest,
  UpdateOrderRequest,
  CreateTransactionRequest,
  UpdateTransactionRequest,
  UpdatePortfolioBalanceRequest,
  FundamentalsResponse,
  UpdateProfileRequest,
  ChangePasswordRequest,
  SectorDto,
  IndustryDto,
  MarketIndex,
  MarketIndexHistoryRange,
  MarketIndexHistoryResponse,
  MarketIndexRefreshResponse,
  CreateSectorRequest,
  UpdateSectorRequest,
  CreateIndustryRequest,
  UpdateIndustryRequest,
  MoveIndustryRequest,
  CreateMarketIndexRequest,
  UpdateMarketIndexRequest,
  IndexConstituentsResponse,
  IndexConstituentsRefreshResponse,
  IndexConstituentHistoryRefreshJobResponse,
  IndexConstituentHistoryRefreshBatchResponse,
  IndexConstituentsBatchQuoteRefreshJobResponse,
  IndexConstituentPerformanceResponse,
  StockCatalogPerformanceResponse,
  TechnicalAnalysisResponse,
  StockMetadataEnrichmentJob,
  StockMetadataEnrichmentResultPage,
  StockMetadataEnrichmentScope,
} from '../types';

export const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

const api = axios.create({
  baseURL: API_BASE,
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers.Authorization = 'Bearer ' + token;
  }
  return config;
});

api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('token');
      window.location.href = '/financeapp/login';
    }
    return Promise.reject(error);
  }
);

// Auth
export const login = (data: LoginRequest) =>
  api.post<{ token: string }>('/Auth/login', data);

export const register = (data: RegisterRequest) =>
  api.post('/Users/register', data);

// Users
export const getMe = () => api.get<User>('/Users/me');
export const deleteMe = () => api.delete('/Users/me');
export const updateMyProfile = (data: UpdateProfileRequest) => api.patch<User>('/Users/me/profile', data);
export const changeMyPassword = (data: ChangePasswordRequest) => api.post('/Users/me/change-password', data);

// Portfolios
export const getPortfolios = () => api.get<Portfolio[]>('/Portfolios');
export const getPortfolio = (id: number) => api.get<Portfolio>(`/Portfolios/${id}`);
export const createPortfolio = (data: CreatePortfolioRequest) => api.post<Portfolio>('/Portfolios', data);
export const deletePortfolio = (id: number) => api.delete(`/Portfolios/${id}`);
export const addPortfolioItem = (portfolioId: number, data: AddPortfolioItemRequest) =>
  api.post(`/Portfolios/${portfolioId}/items`, data);
export const updatePortfolioItem = (portfolioId: number, itemId: number, data: AddPortfolioItemRequest) =>
  api.put(`/Portfolios/${portfolioId}/items/${itemId}`, data);
export const deletePortfolioItem = (portfolioId: number, itemId: number) =>
  api.delete(`/Portfolios/${portfolioId}/items/${itemId}`);

// Orders
export const getOrders = (portfolioId: number) =>
  api.get<Order[]>(`/Portfolios/${portfolioId}/orders`);
export const createOrder = (portfolioId: number, data: CreateOrderRequest) =>
  api.post<Order>(`/Portfolios/${portfolioId}/orders`, data);
export const updateOrder = (portfolioId: number, orderId: number, data: UpdateOrderRequest) =>
  api.put<Order>(`/Portfolios/${portfolioId}/orders/${orderId}`, data);
export const deleteOrder = (portfolioId: number, orderId: number) =>
  api.delete(`/Portfolios/${portfolioId}/orders/${orderId}`);

// Stocks
export const getStocks = () => api.get<Stock[]>('/Stocks');
export const getTrackedStocks = () => api.get<Stock[]>('/Stocks/tracked');
export const getStockCatalog = () => api.get<Stock[]>('/Stocks/catalog');
export const getStockCatalogPerformance = (range: StockHistoryRange, signal?: AbortSignal) =>
  api.get<StockCatalogPerformanceResponse>('/Stocks/catalog/performance', { params: { range }, signal });
export const getStock = (id: number) => api.get<Stock>(`/Stocks/${id}`);
export const createStock = (data: CreateStockRequest) => api.post<Stock>('/Stocks', data);
export const updateStock = (id: number, data: UpdateStockRequest) => api.put<Stock>(`/Stocks/${id}`, data);
export const updateStockMetadata = (id: number, data: UpdateStockMetadataRequest) => api.put<void>(`/Stocks/${id}/metadata`, data);
export const updateStockQuote = (id: number, data: UpdateStockQuoteRequest) =>
  api.patch<UpdateStockQuoteResponse>(`/Stocks/${id}/quote`, data);
export const deleteStock = (id: number) => api.delete(`/Stocks/${id}`);
export const getStockHistory = (id: number, range: StockHistoryRange) =>
  api.get<StockHistoryResponse>(`/Stocks/${id}/history`, { params: { range } });
export const refreshStockHistory = (id: number) =>
  api.post<StockHistoryRefreshResponse>(`/Stocks/${id}/history/refresh`);
export const getStockHistoryRoutingDiagnostics = (id: number) =>
  api.get<StockHistoryRepairDiagnosticsResponse>(`/Stocks/${id}/history/routing-diagnostics`);
export const validateStockHistoryProviderSymbol = (id: number, candidateProviderSymbol?: string | null) =>
  api.post<StockHistoryRepairDiagnosticsResponse>(`/Stocks/${id}/history/provider-symbol/validate`, {
    candidateProviderSymbol: candidateProviderSymbol ?? null,
  });
export const hardResetStockHistory = (
  id: number,
  confirmationText: string,
  candidateProviderSymbol?: string | null,
) =>
  api.post<StockHistoryRepairDiagnosticsResponse>(`/Stocks/${id}/history/hard-reset`, {
    candidateProviderSymbol: candidateProviderSymbol ?? null,
    confirmationText,
  });
export const getStockFundamentals = (id: number) =>
  api.get<FundamentalsResponse>(`/Stocks/${id}/fundamentals`);
export const refreshStockFundamentals = (id: number) =>
  api.post<FundamentalsResponse>(`/Stocks/${id}/fundamentals/refresh`);
export const getStockTechnicalAnalysis = (id: number, signal?: AbortSignal) =>
  api.get<TechnicalAnalysisResponse>(`/Stocks/${id}/technical-analysis`, { signal });

// Stock prices
export const getStockPrice = (symbol: string, exchange: StockExchange, finanzenNetSlug?: string | null) =>
  api.get<StockQuoteResponse>(`/StockPrice/${encodeURIComponent(symbol)}`, {
    params: { exchange, ...(finanzenNetSlug ? { finanzenNetSlug } : {}) },
  });
export const getEurUsdRate = () =>
  api.get<{ eurUsd: number }>('/StockPrice/rate/eurusd');

// Finance
export const getBalance = (portfolioId: number) =>
  api.get<PortfolioBalance>(`/Portfolios/${portfolioId}/finance/balance`);
export const updateBalance = (portfolioId: number, data: UpdatePortfolioBalanceRequest) =>
  api.put<PortfolioBalance>(`/Portfolios/${portfolioId}/finance/balance`, data);
export const getTransactions = (portfolioId: number) =>
  api.get<Transaction[]>(`/Portfolios/${portfolioId}/finance/transactions`);
export const createTransaction = (portfolioId: number, data: CreateTransactionRequest) =>
  api.post<Transaction>(`/Portfolios/${portfolioId}/finance/transactions`, data);
export const updateTransaction = (portfolioId: number, id: number, data: UpdateTransactionRequest) =>
  api.put<Transaction>(`/Portfolios/${portfolioId}/finance/transactions/${id}`, data);
export const deleteTransaction = (portfolioId: number, id: number) =>
  api.delete(`/Portfolios/${portfolioId}/finance/transactions/${id}`);
export const getDividends = (portfolioId: number) =>
  api.get<Dividend[]>(`/Portfolios/${portfolioId}/finance/dividends`);
export const createDividend = (portfolioId: number, data: { stockId: number; amount: number; paidAt: string }) =>
  api.post<Dividend>(`/Portfolios/${portfolioId}/finance/dividends`, data);
export const deleteDividend = (portfolioId: number, id: number) =>
  api.delete(`/Portfolios/${portfolioId}/finance/dividends/${id}`);

// Sectors API
type SectorApiItem = SectorDto & {
  StockCount?: number;
  IndustryCount?: number;
  Industries?: IndustryApiItem[];
};

type IndustryApiItem = IndustryDto & {
  StockCount?: number;
};

const toNumberOrZero = (value: unknown): number => {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
};

const normalizeIndustryDto = (industry: IndustryApiItem): IndustryDto => ({
  ...industry,
  stockCount: toNumberOrZero(industry.stockCount ?? industry.StockCount),
});

const normalizeSectorDto = (sector: SectorApiItem): SectorDto => ({
  ...sector,
  industryCount: toNumberOrZero(sector.industryCount ?? sector.IndustryCount),
  stockCount: toNumberOrZero(sector.stockCount ?? sector.StockCount),
  industries: Array.isArray(sector.industries)
    ? sector.industries.map(normalizeIndustryDto)
    : Array.isArray(sector.Industries)
      ? sector.Industries.map(normalizeIndustryDto)
      : [],
});

export const getSectors = (includeArchived = false) =>
  api.get<SectorApiItem[]>('/sectors', { params: { includeArchived } }).then((r) =>
    (Array.isArray(r.data) ? r.data : []).map(normalizeSectorDto),
  );

export const createSector = (req: CreateSectorRequest) =>
  api.post<SectorDto>('/sectors', req).then((r) => r.data);

export const updateSector = (id: number, req: UpdateSectorRequest) =>
  api.put<SectorDto>(`/sectors/${id}`, req).then((r) => r.data);

export const archiveSector = (id: number) =>
  api.post<void>(`/sectors/${id}/archive`).then((r) => r.data);

export const restoreSector = (id: number) =>
  api.post<void>(`/sectors/${id}/restore`).then((r) => r.data);

export const deleteSector = (id: number) =>
  api.delete<void>(`/sectors/${id}`).then((r) => r.data);

export const createIndustry = (sectorId: number, req: CreateIndustryRequest) =>
  api.post<IndustryDto>(`/sectors/${sectorId}/industries`, req).then((r) => r.data);

export const updateIndustry = (sectorId: number, industryId: number, req: UpdateIndustryRequest) =>
  api.put<IndustryDto>(`/sectors/${sectorId}/industries/${industryId}`, req).then((r) => r.data);

export const archiveIndustry = (sectorId: number, industryId: number) =>
  api.post<void>(`/sectors/${sectorId}/industries/${industryId}/archive`).then((r) => r.data);

export const restoreIndustry = (sectorId: number, industryId: number) =>
  api.post<void>(`/sectors/${sectorId}/industries/${industryId}/restore`).then((r) => r.data);

export const deleteIndustry = (sectorId: number, industryId: number) =>
  api.delete<void>(`/sectors/${sectorId}/industries/${industryId}`).then((r) => r.data);

export const moveIndustry = (sectorId: number, industryId: number, req: MoveIndustryRequest) =>
  api.patch<IndustryDto>(`/sectors/${sectorId}/industries/${industryId}/move`, req).then((r) => r.data);

// Market indices API
export const getMarketIndices = (includeArchived = false) =>
  api.get<MarketIndex[]>('/market-indices', { params: { includeArchived } }).then((r) => r.data);

export const createMarketIndex = (req: CreateMarketIndexRequest) =>
  api.post<MarketIndex>('/market-indices', req).then((r) => r.data);

export const updateMarketIndex = (id: number, req: UpdateMarketIndexRequest) =>
  api.put<MarketIndex>(`/market-indices/${id}`, req).then((r) => r.data);

export const archiveMarketIndex = (id: number) =>
  api.post<void>(`/market-indices/${id}/archive`).then((r) => r.data);

export const restoreMarketIndex = (id: number) =>
  api.post<void>(`/market-indices/${id}/restore`).then((r) => r.data);

export const deleteMarketIndex = (id: number) =>
  api.delete<void>(`/market-indices/${id}`).then((r) => r.data);

export const getMarketIndexHistory = (id: number, range: MarketIndexHistoryRange) =>
  api.get<MarketIndexHistoryResponse>(`/market-indices/${id}/history`, { params: { range } });

export const refreshMarketIndexHistory = (id: number) =>
  api.post<MarketIndexRefreshResponse>(`/market-indices/${id}/history/refresh`);

// Index constituents
export const getIndexConstituents = (id: number, includeFormer = false) =>
  api.get<IndexConstituentsResponse>(`/market-indices/${id}/constituents`, { params: { includeFormer } });

export const refreshIndexConstituents = (id: number) =>
  api.post<IndexConstituentsRefreshResponse>(`/market-indices/${id}/constituents/refresh`);

export const getIndexConstituentHistory = (indexId: number, stockId: number, range: StockHistoryRange) =>
  api.get<StockHistoryResponse>(`/market-indices/${indexId}/constituents/${stockId}/history`, { params: { range } });

export const refreshIndexConstituentHistory = (indexId: number, stockId: number) =>
  api.post<IndexConstituentHistoryRefreshJobResponse>(`/market-indices/${indexId}/constituents/${stockId}/history/refresh`);

export const getIndexConstituentHistoryRefreshJob = (indexId: number, stockId: number, jobId: string) =>
  api.get<IndexConstituentHistoryRefreshJobResponse>(
    `/market-indices/${indexId}/constituents/${stockId}/history/refresh-jobs/${jobId}`,
  );

export const refreshIndexConstituentsHistory = (indexId: number) =>
  api.post<IndexConstituentHistoryRefreshBatchResponse>(`/market-indices/${indexId}/constituents/history/refresh`);

export const startIndexConstituentsBatchQuoteRefresh = (indexId: number) =>
  api.post<IndexConstituentsBatchQuoteRefreshJobResponse>(`/market-indices/${indexId}/constituents/quotes/refresh`);

export const getIndexConstituentsBatchQuoteRefreshJob = (indexId: number, jobId: string) =>
  api.get<IndexConstituentsBatchQuoteRefreshJobResponse>(
    `/market-indices/${indexId}/constituents/quotes/refresh-jobs/${jobId}`,
  );

export const getIndexConstituentPerformance = (
  indexId: number,
  range: StockHistoryRange,
  signal?: AbortSignal,
) =>
  api.get<IndexConstituentPerformanceResponse>(
    `/market-indices/${indexId}/constituents/performance`,
    { params: { range }, signal },
  );

export const trackStock = (id: number) =>
  api.post<Stock>(`/stocks/${id}/track`);
export const untrackStock = (id: number) =>
  api.post<Stock>(`/stocks/${id}/untrack`);

export const createStockMetadataEnrichmentJob = (
  scope: StockMetadataEnrichmentScope,
  dryRun: boolean,
  selectedStockIds?: number[],
) =>
  api.post<{ jobId: string }>('/stocks/metadata-enrichment/jobs', {
    scope,
    dryRun,
    selectedStockIds,
  });

export const getStockMetadataEnrichmentJob = (jobId: string) =>
  api.get<StockMetadataEnrichmentJob>(`/stocks/metadata-enrichment/jobs/${jobId}`);

export const getStockMetadataEnrichmentResults = (
  jobId: string,
  page = 1,
  pageSize = 50,
  decision?: string,
) =>
  api.get<StockMetadataEnrichmentResultPage>(`/stocks/metadata-enrichment/jobs/${jobId}/results`, {
    params: { page, pageSize, decision },
  });

export const applyStockMetadataEnrichmentJob = (jobId: string, onlyManuallyApproved = false) =>
  api.post(`/stocks/metadata-enrichment/jobs/${jobId}/apply`, { onlyManuallyApproved });

export const reviewStockMetadataEnrichmentResult = (
  jobId: string,
  resultId: number,
  approve: boolean,
  industryId?: number,
  saveMapping = false,
) =>
  api.post(`/stocks/metadata-enrichment/jobs/${jobId}/results/${resultId}/review`, {
    approve,
    industryId,
    saveMapping,
  });

export default api;
