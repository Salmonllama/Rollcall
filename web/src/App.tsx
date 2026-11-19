// SPDX-License-Identifier: GPL-3.0-or-later
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { apiRequest } from './api/client';
import type { CurrentUserDto } from './api/types';
import { AppShell } from './components/AppShell';
import { ComponentsReferencePage } from './dev/ComponentsReferencePage';
import {
  AgentsPage,
  ApprovalRulesPage,
  ConnectionsPage,
  HealthPage,
  HelpPage,
  SettingsPage,
  TeamPage,
  TemplatesPage,
} from './features/admin/AdminPages';
import { DashboardPage } from './features/dashboard/DashboardPage';
import {
  ConditionalAccessPage,
  GroupsPage,
  LicensesPage,
} from './features/directory/DirectoryManagementPages';
import { OffboardingPage, OnboardingPage } from './features/lifecycle/LifecyclePages';
import {
  ApprovalsPage,
  AuditPage,
  RunDetailPage,
  RunsPage,
} from './features/runs/RunsAndApprovalsPages';
import { NoAccessPage, SetupWizardPage, SignInPage } from './features/setup/SetupWizardPage';
import { UserDetailPage, UsersPage } from './features/users/UsersAndDetailPages';
import { ThemeProvider } from './theme/ThemeProvider';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
      staleTime: 15_000,
    },
  },
});

function AuthenticatedRoutes() {
  const meQuery = useQuery({
    queryKey: ['me'],
    queryFn: () => apiRequest<CurrentUserDto>('/api/me'),
  });

  if (import.meta.env.DEV && window.location.pathname === '/dev/components') {
    return (
      <main className="rc-content">
        <ComponentsReferencePage />
      </main>
    );
  }

  if (meQuery.isLoading) {
    return (
      <main className="rc-content">
        <p className="rc-muted">Loading Rollcall…</p>
      </main>
    );
  }

  const me = meQuery.data;
  if (!me || !me.setupCompleted) {
    return <SetupWizardPage onComplete={() => void meQuery.refetch()} />;
  }

  if (!me.isAuthenticated) {
    return <SignInPage tenantDomain={me.tenantDomain} />;
  }

  if (me.disabled || me.roles.length === 0) {
    return <NoAccessPage upn={me.upn} />;
  }

  const isAdmin = me.roles.includes('Admin');

  return (
    <AppShell user={me} onSignedOut={() => void meQuery.refetch()}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/users" element={<UsersPage />} />
        <Route path="/users/:id" element={<UserDetailPage />} />
        <Route path="/onboard" element={<OnboardingPage />} />
        <Route path="/offboard" element={<OffboardingPage />} />
        <Route path="/offboard/:id" element={<OffboardingPage />} />
        <Route path="/runs" element={<RunsPage />} />
        <Route path="/runs/:id" element={<RunDetailPage />} />
        <Route path="/approvals" element={<ApprovalsPage />} />
        <Route path="/groups" element={<GroupsPage />} />
        <Route path="/licenses" element={<LicensesPage />} />
        <Route path="/conditional-access" element={<ConditionalAccessPage isAdmin={isAdmin} />} />
        <Route path="/audit" element={<AuditPage isAdmin={isAdmin} />} />
        <Route path="/admin/templates" element={<TemplatesPage />} />
        <Route path="/admin/approval-rules" element={<ApprovalRulesPage />} />
        <Route path="/admin/team" element={<TeamPage />} />
        <Route path="/admin/connections" element={<ConnectionsPage />} />
        <Route path="/admin/agents" element={<AgentsPage />} />
        <Route path="/admin/settings" element={<SettingsPage />} />
        <Route path="/admin/health" element={<HealthPage />} />
        <Route path="/help" element={<HelpPage />} />
        {import.meta.env.DEV ? (
          <Route path="/dev/components" element={<ComponentsReferencePage />} />
        ) : null}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AppShell>
  );
}

export function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <AuthenticatedRoutes />
        </BrowserRouter>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
