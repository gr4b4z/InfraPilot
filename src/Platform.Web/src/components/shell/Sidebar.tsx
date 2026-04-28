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
  GitPullRequest,
  Inbox,
} from 'lucide-react';
import { useAuthStore } from '@/stores/authStore';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { getAppName, getAppSubtitle, getEnvironmentLabel } from '@/lib/runtimeConfig';

interface NavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ size?: number; className?: string }>;
  badge?: number;
  section?: string;
  adminOnly?: boolean;
  featureFlag?: string;
}

const navItems: NavItem[] = [
  { to: '/catalog', label: 'Service Catalog', icon: LayoutGrid, section: 'main', featureFlag: FeatureFlag.ServiceCatalog },
  { to: '/requests', label: 'My Requests', icon: FileText, section: 'main', featureFlag: FeatureFlag.ServiceCatalog },
  { to: '/approvals', label: 'Approvals', icon: CheckCircle, badge: 0, section: 'main', featureFlag: FeatureFlag.Approvals },
  { to: '/deployments', label: 'Deployments', icon: Rocket, section: 'main' },
  { to: '/promotions', label: 'Promotions', icon: GitPullRequest, section: 'main', featureFlag: FeatureFlag.Promotions },
  { to: '/me/tickets', label: 'My ticket queue', icon: Inbox, section: 'main', featureFlag: FeatureFlag.Promotions },
  { to: '/webhooks', label: 'Webhooks', icon: Webhook, section: 'main', adminOnly: true },
  { to: '/settings', label: 'Settings', icon: Settings, section: 'main', adminOnly: true },
];

export function Sidebar() {
  const [collapsed, setCollapsed] = useState(false);
  const user = useAuthStore((s) => s.user);
  const appName = getAppName();
  const appSubtitle = getAppSubtitle();
  const isAdmin = user?.isAdmin ?? false;
  const flags = useFeatureFlagsStore((s) => s.flags);

  const visibleNavItems = navItems.filter((item) => {
    if (item.adminOnly && !isAdmin) return false;
    if (item.featureFlag && flags[item.featureFlag] === false) return false;
    return true;
  });

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

      {/* Environment badge — only shown when ENVIRONMENT_LABEL is set in config.json */}
      {!collapsed && getEnvironmentLabel() && (
        <div className="px-3 pt-3 pb-1">
          <div
            className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-[11px] font-medium"
            style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
          >
            <div className="w-1.5 h-1.5 rounded-full" style={{ backgroundColor: 'var(--warning)' }} />
            {getEnvironmentLabel()}
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
        <button
          onClick={() => setCollapsed(!collapsed)}
          className="w-full flex items-center justify-center h-8 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
          style={{ color: 'var(--text-muted)' }}
        >
          {collapsed ? <ChevronRight size={14} /> : <ChevronLeft size={14} />}
        </button>
        {!collapsed && (
          <p
            className="text-[10px] text-center mt-1 font-mono"
            style={{ color: 'var(--text-muted)' }}
            title="Build version"
          >
            {__APP_VERSION__}
          </p>
        )}
      </div>
    </aside>
  );
}
