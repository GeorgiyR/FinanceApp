import React from 'react';
import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { ConfigProvider } from 'antd';
import ruRU from 'antd/locale/ru_RU';
import { AuthProvider } from './contexts/AuthContext';
import PrivateRoute from './components/PrivateRoute';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import DashboardPage from './pages/DashboardPage';
import PortfolioDetailPage from './pages/PortfolioDetailPage';
import StocksPage from './pages/StocksPage';
import SectorsPage from './pages/SectorsPage';
import MarketIndicesPage from './pages/MarketIndicesPage';
import FinancialMetricsPage from './pages/FinancialMetricsPage';
import ProfilePage from './pages/ProfilePage';
import HelpPage from './pages/HelpPage';
import SystemProcessRunsPage from './pages/SystemProcessRunsPage';

const App: React.FC = () => {
  return (
    <ConfigProvider locale={ruRU}>
      <BrowserRouter basename="/financeapp">
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />
            <Route
              path="/"
              element={
                <PrivateRoute>
                  <DashboardPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/portfolios/:id"
              element={
                <PrivateRoute>
                  <PortfolioDetailPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/stocks"
              element={
                <PrivateRoute>
                  <StocksPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/stocks/catalog"
              element={
                <PrivateRoute>
                  <StocksPage mode="catalog" />
                </PrivateRoute>
              }
            />
            <Route
              path="/sectors"
              element={
                <PrivateRoute>
                  <SectorsPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/market-indices"
              element={
                <PrivateRoute>
                  <MarketIndicesPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/market-indices/:id"
              element={
                <PrivateRoute>
                  <MarketIndicesPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/financial-metrics"
              element={
                <PrivateRoute>
                  <FinancialMetricsPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/help/:articleSlug"
              element={
                <PrivateRoute>
                  <HelpPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/help"
              element={
                <PrivateRoute>
                  <HelpPage />
                </PrivateRoute>
              }
            />
            <Route
              path="/profile"
              element={
                <PrivateRoute>
                  <ProfilePage />
                </PrivateRoute>
              }
            />
            <Route
              path="/system/processes"
              element={
                <PrivateRoute>
                  <SystemProcessRunsPage />
                </PrivateRoute>
              }
            />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </ConfigProvider>
  );
};

export default App;
