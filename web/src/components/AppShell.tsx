// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, NavLink, useNavigate } from 'react-router-dom';
import {
  Activity,
  ClipboardList,
  FolderTree,
  HelpCircle,
  LayoutDashboard,
  LogOut,
  Menu,
  Moon,
  PanelLeftClose,
  PanelLeftOpen,
  ScrollText,
  Search,
  Settings,
  ShieldCheck,
  Stamp,
  Sun,
  Ticket,
  UserMinus,
  UserPlus,
  Users,
} from 'lucide-react';
import logoDark from '../../../docs/brand/rollcall-logo-dark.svg';
import logoLight from '../../../docs/brand/rollcall-logo.svg';
import markSvg from '../../../docs/brand/rollcall-mark.svg';
import { apiRequest } from '../api/client';
import type { CurrentUserDto } from '../api/types';
import { useTheme } from '../theme/themeState';
import { AlertBanner, Button } from './Primitives';

export function AppShell({
  user,
  onSignedOut,
  children,
}: {
  user: CurrentUserDto;
  onSignedOut: () => void;
  children: ReactNode;
}) {
  const { theme, setTheme, resolvedTheme, density, setDensity } = useTheme();
  const [rail, setRail] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const searchInputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();

  const isAdmin = user.roles.includes('Admin');
  const canRun = user.roles.includes('Technician') || isAdmin;
  const features = user.features;

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (
        e.key === '/' &&
        document.activeElement?.tagName !== 'INPUT' &&
        document.activeElement?.tagName !== 'TEXTAREA' &&
        document.activeElement?.tagName !== 'SELECT'
      ) {
        e.preventDefault();
        searchInputRef.current?.focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, []);

  const handleGlobalSearch = (e: React.SyntheticEvent) => {
    e.preventDefault();
    const q = searchQuery.trim();
    void navigate(q ? `/users?q=${encodeURIComponent(q)}` : '/users');
  };

  const handleSignOut = () => {
    void apiRequest('/api/auth/logout', { method: 'POST' }).finally(() => {
      onSignedOut();
    });
  };

  const navItems: { to: string; label: string; icon: ReactNode; visible: boolean }[] = [
    {
      to: '/',
      label: 'Dashboard',
      icon: <LayoutDashboard size={20} aria-hidden="true" />,
      visible: true,
    },
    { to: '/users', label: 'Users', icon: <Users size={20} aria-hidden="true" />, visible: true },
    {
      to: '/onboard',
      label: 'Onboarding',
      icon: <UserPlus size={20} aria-hidden="true" />,
      visible: canRun,
    },
    {
      to: '/offboard',
      label: 'Offboarding',
      icon: <UserMinus size={20} aria-hidden="true" />,
      visible: canRun,
    },
    {
      to: '/groups',
      label: 'Groups',
      icon: <FolderTree size={20} aria-hidden="true" />,
      visible: true,
    },
    {
      to: '/licenses',
      label: 'Licenses',
      icon: <Ticket size={20} aria-hidden="true" />,
      visible: features.licenses !== false,
    },
    {
      to: '/conditional-access',
      label: 'Conditional Access',
      icon: <ShieldCheck size={20} aria-hidden="true" />,
      visible: features.conditionalAccess !== false,
    },
    {
      to: '/approvals',
      label: 'Approvals',
      icon: <Stamp size={20} aria-hidden="true" />,
      visible: true,
    },
    { to: '/runs', label: 'Runs', icon: <Activity size={20} aria-hidden="true" />, visible: true },
    {
      to: '/audit',
      label: 'Audit log',
      icon: <ScrollText size={20} aria-hidden="true" />,
      visible: true,
    },
  ];

  const adminItems: { to: string; label: string; icon: ReactNode }[] = [
    {
      to: '/admin/templates',
      label: 'Templates',
      icon: <ClipboardList size={20} aria-hidden="true" />,
    },
    { to: '/admin/settings', label: 'Settings', icon: <Settings size={20} aria-hidden="true" /> },
  ];

  return (
    <div className="rc-shell" data-rail={rail ? 'true' : 'false'}>
      <aside
        className="rc-sidebar"
        data-drawer-open={drawerOpen ? 'true' : 'false'}
        aria-label="Primary navigation"
      >
        <div className="rc-sidebar-brand">
          <Link to="/" aria-label="Rollcall home">
            <img
              src={rail ? markSvg : resolvedTheme === 'dark' ? logoDark : logoLight}
              alt="Rollcall"
              className="rc-sidebar-logo"
            />
          </Link>
          <Button
            variant="ghost"
            compact
            aria-label={rail ? 'Expand sidebar' : 'Collapse sidebar'}
            onClick={() => {
              setRail((r) => !r);
            }}
          >
            {rail ? (
              <PanelLeftOpen size={18} aria-hidden="true" />
            ) : (
              <PanelLeftClose size={18} aria-hidden="true" />
            )}
          </Button>
        </div>

        <nav className="rc-nav-group">
          {navItems
            .filter((item) => item.visible)
            .map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.to === '/'}
                className="rc-nav-link"
                onClick={() => {
                  setDrawerOpen(false);
                }}
              >
                {item.icon}
                {!rail ? <span className="rc-nav-label">{item.label}</span> : null}
              </NavLink>
            ))}
        </nav>

        {isAdmin ? (
          <>
            <hr className="rc-nav-divider" />
            {!rail ? <div className="rc-nav-section-label">Admin</div> : null}
            <nav className="rc-nav-group" aria-label="Admin navigation">
              {adminItems.map((item) => (
                <NavLink
                  key={item.to}
                  to={item.to}
                  className="rc-nav-link"
                  onClick={() => {
                    setDrawerOpen(false);
                  }}
                >
                  {item.icon}
                  {!rail ? <span className="rc-nav-label">{item.label}</span> : null}
                </NavLink>
              ))}
            </nav>
          </>
        ) : null}

        <div className="rc-sidebar-footer">
          <NavLink
            to="/help"
            className="rc-nav-link"
            onClick={() => {
              setDrawerOpen(false);
            }}
          >
            <HelpCircle size={20} aria-hidden="true" />
            {!rail ? <span className="rc-nav-label">Help</span> : null}
          </NavLink>

          {!rail ? (
            <div className="rc-stack" style={{ padding: 'var(--rc-space-2)' }}>
              <div style={{ fontSize: 'var(--rc-text-sm)', fontWeight: 500 }}>
                {user.displayName ?? user.upn}
              </div>
              <div className="rc-mono rc-muted" style={{ fontSize: 'var(--rc-text-xs)' }}>
                {user.roles.join(', ')}
              </div>
              <div className="rc-row">
                <Button
                  variant="ghost"
                  compact
                  aria-label="Switch theme"
                  onClick={() => {
                    setTheme(theme === 'system' ? 'light' : theme === 'light' ? 'dark' : 'system');
                  }}
                >
                  {resolvedTheme === 'dark' ? (
                    <Moon size={16} aria-hidden="true" />
                  ) : (
                    <Sun size={16} aria-hidden="true" />
                  )}
                  Theme: {theme}
                </Button>
                <Button
                  variant="ghost"
                  compact
                  onClick={() => {
                    setDensity(density === 'default' ? 'compact' : 'default');
                  }}
                >
                  Rows: {density}
                </Button>
              </div>
              <Button variant="ghost" compact onClick={handleSignOut}>
                <LogOut size={16} aria-hidden="true" />
                Sign out
              </Button>
            </div>
          ) : null}
        </div>
      </aside>

      <div className="rc-main-column">
        <header className="rc-topbar">
          <div className="rc-topbar-left">
            <Button
              variant="ghost"
              compact
              className="rc-mobile-menu-btn"
              aria-label="Open navigation menu"
              onClick={() => {
                setDrawerOpen((o) => !o);
              }}
            >
              <Menu size={20} aria-hidden="true" />
            </Button>
            <form onSubmit={handleGlobalSearch} style={{ width: '100%' }} role="search">
              <label htmlFor="rc-global-search" className="rc-sr-only">
                Search users
              </label>
              <div className="rc-row" style={{ flexWrap: 'nowrap', gap: 'var(--rc-space-2)' }}>
                <Search size={18} color="var(--rc-text-muted)" aria-hidden="true" />
                <input
                  id="rc-global-search"
                  ref={searchInputRef}
                  className="rc-input"
                  value={searchQuery}
                  onChange={(e) => {
                    setSearchQuery(e.target.value);
                  }}
                  placeholder="Search users by name, UPN, or employee ID (/)"
                />
              </div>
            </form>
          </div>
          <div className="rc-topbar-right">
            <Link to="/runs" className="rc-btn rc-btn-ghost rc-btn-compact">
              <Activity size={16} aria-hidden="true" />
              Jobs
            </Link>
            {user.tenantDomain ? (
              <span className="rc-tenant-chip" title="Connected Microsoft 365 tenant">
                {user.tenantDomain}
              </span>
            ) : null}
          </div>
        </header>

        <main className="rc-content">
          {user.showCertExpiryWarning && isAdmin ? (
            <AlertBanner
              tone="warning"
              title="App certificate expiring soon"
              action={
                <Link to="/admin/connections" className="rc-btn rc-btn-secondary rc-btn-compact">
                  Rotate certificate
                </Link>
              }
            >
              The app certificate expires in {String(user.certDaysUntilExpiry ?? 0)} days. Rotate it
              now to avoid an outage.
            </AlertBanner>
          ) : null}
          {children}
        </main>
      </div>
    </div>
  );
}
