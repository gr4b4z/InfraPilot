import { useState } from 'react';
import { NavLink } from 'react-router-dom';
import {
  LayoutGrid,
  FileText,
  CheckCircle,
  ChevronLeft,
  ChevronRight,
  Settings,
  Zap,
  Rocket,
  Webhook,
} from 'lucide-react';
import { useAuthStore } from '@/stores/authStore';
import { getAppName, getAppSubtitle } from '@/lib/runtimeConfig';

interface NavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ size?: number; className?: string }>;
  badge?: number;
  section?: string;
  adminOnly?: boolean;
}

const navItems: NavItem[] = [
  { to: '/catalog', label: 'Service Catalog', icon: LayoutGrid, section: 'main' },
  { to: '/requests', label: 'My Requests', icon: FileText, section: 'main' },
  { to: '/approvals', label: 'Approvals', icon: CheckCircle, badge: 0, section: 'main' },
  { to: '/deployments', label: 'Deployments', icon: Rocket, section: 'main' },
  { to: '/webhooks', label: 'Webhooks', icon: Webhook, section: 'main', adminOnly: true },
  { to: '/settings', label: 'Settings', icon: Settings, section: 'main', adminOnly: true },
];

export function Sidebar() {
  const [collapsed, setCollapsed] = useState(false);
  const user = useAuthStore((s) => s.user);
  const appName = getAppName();
  const appSubtitle = getAppSubtitle();
  const isAdmin = user?.isAdmin ?? false;

  const visibleNavItems = navItems.filter((item) => !item.adminOnly || isAdmin);

  return (
    <aside
      className={`flex flex-col border-r transition-all duration-200 shrink-0 ${
        collapsed ? 'w-[60px]' : 'w-[240px]'
      }`}
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-secondary)',
      }}
    >
      {/* Logo area */}
      <div
        className="flex items-center h-14 px-4 border-b shrink-0"
        style={{ borderColor: 'var(--border-color)' }}
      >
        {!collapsed ? (
          <div className="flex items-center gap-2.5">
            <div
              className="w-7 h-7 rounded-lg flex items-center justify-center"
              style={{ background: 'linear-gradient(135deg, var(--color-swo-purple), var(--color-swo-cyan))' }}
            >
              <Zap size={14} className="text-white" />
            </div>
            <div className="flex flex-col">
              <span
                className="font-semibold text-[13px] leading-tight tracking-tight"
                style={{ color: 'var(--text-primary)' }}
              >
                {appName}
              </span>
              <span className="text-[10px] leading-tight" style={{ color: 'var(--text-muted)' }}>
                {appSubtitle}
              </span>
            </div>
          </div>
        ) : (
          <div
            className="w-7 h-7 rounded-lg flex items-center justify-center mx-auto"
            style={{ background: 'linear-gradient(135deg, var(--color-swo-purple), var(--color-swo-cyan))' }}
          >
            <Zap size={14} className="text-white" />
          </div>
        )}
      </div>

      {/* Environment badge */}
      {!collapsed && (
        <div className="px-3 pt-3 pb-1">
          <div
            className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[11px] font-medium"
            style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
          >
            <div className="w-1.5 h-1.5 rounded-full" style={{ backgroundColor: 'var(--warning)' }} />
            Development
          </div>
        </div>
      )}

      {/* Navigation */}
      <nav className="flex-1 py-2 px-2">
        {!collapsed && (
          <div className="px-2 pt-2 pb-1.5">
            <span className="text-[10px] font-semibold uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
              Platform
            </span>
          </div>
        )}
        <div className="space-y-0.5">
          {visibleNavItems.map((item) => {
            const Icon = item.icon;
            return (
              <NavLink
                key={item.to}
                to={item.to}
                className={({ isActive }) =>
                  `group flex items-center gap-2.5 px-2.5 py-2 rounded-lg text-[13px] font-medium transition-all duration-150 ${
                    collapsed ? 'justify-center' : ''
                  } ${
                    isActive
                      ? ''
                      : 'hover:bg-[var(--accent-muted)]'
                  }`
                }
                style={({ isActive }) => ({
                  backgroundColor: isActive ? 'var(--accent-subtle)' : undefined,
                  color: isActive ? 'var(--accent)' : 'var(--text-secondary)',
                })}
                title={collapsed ? item.label : undefined}
              >
                <Icon size={18} className="shrink-0" />
                {!collapsed && (
                  <>
                    <span className="flex-1">{item.label}</span>
                    {item.badge !== undefined && item.badge > 0 && (
                      <span
                        className="badge text-white"
                        style={{ backgroundColor: 'var(--accent)' }}
                      >
                        {item.badge}
                      </span>
                    )}
                  </>
                )}
              </NavLink>
            );
          })}
        </div>
      </nav>

      {/* Bottom section */}
      <div className="border-t px-2 py-2" style={{ borderColor: 'var(--border-color)' }}>
        {!collapsed && (
          <div
            className="flex items-center gap-2.5 px-2.5 py-2 rounded-lg mb-1"
            style={{ backgroundColor: 'var(--accent-muted)' }}
          >
            <div
              className="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-bold text-white shrink-0"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              {user?.initials ?? 'DU'}
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-1.5">
                <p className="text-[12px] font-medium truncate" style={{ color: 'var(--text-primary)' }}>
                  {user?.name ?? 'Dev User'}
                </p>
                {user?.isAdmin && (
                  <span
                    className="text-[9px] font-semibold px-1.5 py-0.5 rounded-full uppercase"
                    style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
                  >
                    Admin
                  </span>
                )}
              </div>
              <p className="text-[10px] truncate" style={{ color: 'var(--text-muted)' }}>
                {user?.email ?? ''}
              </p>
            </div>
          </div>
        )}
        <button
          onClick={() => setCollapsed(!collapsed)}
          className="w-full flex items-center justify-center h-8 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
          style={{ color: 'var(--text-muted)' }}
        >
          {collapsed ? <ChevronRight size={14} /> : <ChevronLeft size={14} />}
        </button>
      </div>
    </aside>
  );
}
