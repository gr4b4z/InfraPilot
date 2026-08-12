import { useEffect } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import {
  LayoutGrid,
  FileText,
  CheckCircle,
  ChartColumn,
  ChevronLeft,
  ChevronRight,
  Settings,
  X,
  Zap,
  Rocket,
  Webhook,
  GitPullRequest,
  Inbox,
  ScrollText,
  Undo2,
} from 'lucide-react';
import { useAuthStore } from '@/stores/authStore';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';
import { useMyTasksStore } from '@/stores/myTasksStore';
import { useUiStore } from '@/stores/uiStore';
import { useIsDesktop } from '@/hooks/useMediaQuery';
import { getAppName, getAppSubtitle, getEnvironmentLabel } from '@/lib/runtimeConfig';
import { KeyboardHints } from './KeyboardHints';

/**
 * Live "assigned to me" counters, resolved at render from the shared My-tasks rollup rather than
 * baked into {@link navGroups}. Same numbers the topbar bell and the My Tasks page show.
 */
type CounterKey = 'promotionsAwaitingMe' | 'workItemsAssignedToMe';

interface NavItem {
  to: string;
  label: string;
  icon: React.ComponentType<{ size?: number; className?: string }>;
  badge?: number;
  /** Live counter to render as the badge. Takes precedence over the static `badge`. */
  counter?: CounterKey;
  adminOnly?: boolean;
  featureFlag?: string;
}

interface NavGroup {
  label: string;
  featureFlag?: string;
  adminOnly?: boolean;
  items: NavItem[];
}

const navGroups: NavGroup[] = [
  {
    label: 'Catalog',
    featureFlag: FeatureFlag.ServiceCatalog,
    items: [
      { to: '/catalog',   label: 'Service Catalog', icon: LayoutGrid  },
      { to: '/requests',  label: 'My Requests',     icon: FileText    },
      { to: '/approvals', label: 'Approvals',        icon: CheckCircle, badge: 0, featureFlag: FeatureFlag.Approvals },
    ],
  },
  {
    label: 'Deployments',
    items: [
      { to: '/deployments', label: 'Deployments', icon: Rocket },
      { to: '/analytics', label: 'Analytics', icon: ChartColumn, featureFlag: FeatureFlag.Analytics },
      { to: '/release-notes', label: 'Release Notes', icon: ScrollText, featureFlag: FeatureFlag.ReleaseNotes },
    ],
  },
  {
    label: 'Promotions',
    featureFlag: FeatureFlag.Promotions,
    items: [
      { to: '/promotions', label: 'Promotions',    icon: GitPullRequest, counter: 'promotionsAwaitingMe' },
      { to: '/me/work-items', label: 'Work items queue', icon: Inbox,   counter: 'workItemsAssignedToMe' },
    ],
  },
  {
    label: 'Rollbacks',
    featureFlag: FeatureFlag.Rollbacks,
    items: [
      { to: '/rollbacks', label: 'Rollbacks', icon: Undo2 },
    ],
  },
  {
    label: 'System',
    adminOnly: true,
    items: [
      { to: '/webhooks', label: 'Webhooks', icon: Webhook  },
      { to: '/settings', label: 'Settings', icon: Settings },
    ],
  },
];

export function Sidebar() {
  const isDesktop = useIsDesktop();
  const { navDrawerOpen, navCollapsed, setNavDrawerOpen, toggleNavCollapsed } = useUiStore();
  const location = useLocation();
  // The icons-only rail is a desktop affordance. In the drawer there is no width to save — it
  // overlays the content either way — so a collapsed drawer would just be a worse drawer.
  const collapsed = isDesktop && navCollapsed;
  const user = useAuthStore((s) => s.user);
  const appName = getAppName();
  const appSubtitle = getAppSubtitle();
  const isAdmin = user?.isAdmin ?? false;
  const flags = useFeatureFlagsStore((s) => s.flags);
  const promotionsAwaitingMe = useMyTasksStore((s) => s.promotions.length);
  // Both attention slices of the work-items queue: the items this user is answerable for, plus the
  // ones nobody has been put on. Summed so the badge matches the bell, and because the queue page
  // surfaces the two as sibling tabs — a badge counting only one of them would send people to the
  // wrong tab.
  const workItemsAssignedToMe = useMyTasksStore(
    (s) => s.workItems.length + s.unassignedWorkItems.length,
  );
  const counters: Record<CounterKey, number> = { promotionsAwaitingMe, workItemsAssignedToMe };

  const visibleGroups = navGroups
    .filter((g) => {
      if (g.adminOnly && !isAdmin) return false;
      if (g.featureFlag && flags[g.featureFlag] === false) return false;
      return true;
    })
    .map((g) => ({
      ...g,
      items: g.items.filter((item) => {
        if (item.adminOnly && !isAdmin) return false;
        if (item.featureFlag && flags[item.featureFlag] === false) return false;
        return true;
      }),
    }))
    .filter((g) => g.items.length > 0);

  // A navigation drawer that survives the navigation is a drawer covering the page you just asked
  // for. Closing on pathname change also covers the in-page links further down the tree.
  useEffect(() => {
    setNavDrawerOpen(false);
  }, [location.pathname, setNavDrawerOpen]);

  useEffect(() => {
    if (!navDrawerOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setNavDrawerOpen(false);
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [navDrawerOpen, setNavDrawerOpen]);

  return (
    <>
      {/* Scrim behind the drawer. Below `lg` only — at `lg` the sidebar is part of the layout and
          there is nothing to dim. */}
      {navDrawerOpen && (
        <div
          className="fixed inset-0 z-40 lg:hidden"
          style={{ backgroundColor: 'var(--bg-overlay)' }}
          onClick={() => setNavDrawerOpen(false)}
          aria-hidden
        />
      )}

      <aside
        aria-label="Main navigation"
        // Off-canvas overlay below `lg`, an ordinary flex child at `lg` and up. `inert` while
        // parked off-screen so tabbing from the topbar doesn't land in an invisible drawer.
        inert={!isDesktop && !navDrawerOpen}
        className={`flex flex-col border-r shrink-0 z-50 fixed inset-y-0 left-0 w-[260px] max-w-[85vw] transition-transform duration-200 lg:static lg:z-auto lg:max-w-none lg:translate-x-0 lg:transition-all ${
          navDrawerOpen ? 'translate-x-0' : '-translate-x-full'
        } ${collapsed ? 'lg:w-[60px]' : 'lg:w-[240px]'}`}
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

          {/* Dismiss for the drawer. Tapping a nav item closes it too, but a drawer opened by
              accident needs a way out that isn't "navigate somewhere". */}
          <button
            type="button"
            onClick={() => setNavDrawerOpen(false)}
            className="ml-auto p-1.5 -mr-1.5 rounded-lg transition-colors hover:bg-[var(--accent-muted)] lg:hidden"
            style={{ color: 'var(--text-muted)' }}
            aria-label="Close navigation"
          >
            <X size={18} />
          </button>
        </div>

        {/* Environment badge */}
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
        <nav className="flex-1 py-2 px-2 overflow-y-auto">
          {visibleGroups.map((group, groupIdx) => (
            <div
              key={group.label}
              className={groupIdx > 0 ? 'mt-1 pt-1 border-t' : ''}
              style={groupIdx > 0 ? { borderColor: 'var(--border-color)' } : undefined}
            >
              {/* Group label — hidden when collapsed */}
              {!collapsed && (
                <div className="px-2 pt-2 pb-1">
                  <span
                    className="text-[10px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    {group.label}
                  </span>
                </div>
              )}

              <div className="space-y-0.5">
                {group.items.map((item) => {
                  const Icon = item.icon;
                  const count = item.counter ? counters[item.counter] : (item.badge ?? 0);
                  // Counter items say what the number means; a bare digit next to "Promotions"
                  // could just as easily be a total.
                  const countTitle = item.counter
                    ? `${item.label} — ${count} assigned to you`
                    : item.label;
                  return (
                    <NavLink
                      key={item.to}
                      to={item.to}
                      className={({ isActive }) =>
                        // py-2.5 below `lg`: the drawer is driven by thumbs, and a 34px row is
                        // under the ~44px comfortable touch target.
                        `group relative flex items-center gap-2.5 px-2.5 py-2.5 lg:py-2 rounded-lg text-[13px] font-medium transition-all duration-150 ${
                          collapsed ? 'justify-center' : ''
                        } ${isActive ? '' : 'hover:bg-[var(--accent-muted)]'}`
                      }
                      style={({ isActive }) => ({
                        backgroundColor: isActive ? 'var(--accent-subtle)' : undefined,
                        color: isActive ? 'var(--accent)' : 'var(--text-secondary)',
                      })}
                      title={collapsed ? countTitle : undefined}
                    >
                      <Icon size={18} className="shrink-0" />
                      {/* Collapsed rail has no room for the number — keep a dot so a non-zero
                          count is still visible without expanding. */}
                      {collapsed && count > 0 && (
                        <span
                          aria-hidden
                          className="absolute top-1.5 right-1.5 w-2 h-2 rounded-full"
                          style={{ backgroundColor: 'var(--accent)' }}
                        />
                      )}
                      {!collapsed && (
                        <>
                          <span className="flex-1">{item.label}</span>
                          {count > 0 && (
                            <span
                              className="badge text-white"
                              style={{ backgroundColor: 'var(--accent)' }}
                              title={countTitle}
                            >
                              {count > 99 ? '99+' : count}
                            </span>
                          )}
                        </>
                      )}
                    </NavLink>
                  );
                })}
              </div>
            </div>
          ))}
        </nav>

        {/* Bottom section */}
        <div className="border-t px-2 py-2" style={{ borderColor: 'var(--border-color)' }}>
          {/* Rail toggle is desktop-only — see `collapsed` above. */}
          <button
            onClick={toggleNavCollapsed}
            className="w-full hidden lg:flex items-center justify-center h-8 rounded-lg transition-colors hover:bg-[var(--accent-muted)]"
            style={{ color: 'var(--text-muted)' }}
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            {collapsed ? <ChevronRight size={14} /> : <ChevronLeft size={14} />}
          </button>
          {/* Hints only in the expanded sidebar — the 60px rail has no room for them, and they are a
              desktop affordance anyway. */}
          {!collapsed && (
            <>
              <div
                className="hidden lg:block mt-1 pt-2 border-t"
                style={{ borderColor: 'var(--border-color)' }}
              >
                <KeyboardHints />
              </div>
              <p
                className="text-[10px] text-center mt-1 font-mono"
                style={{ color: 'var(--text-muted)' }}
                title="Build version"
              >
                {__APP_VERSION__}
              </p>
            </>
          )}
        </div>
      </aside>
    </>
  );
}
