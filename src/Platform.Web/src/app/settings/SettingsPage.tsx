import { NavLink, Outlet } from 'react-router-dom';
import {
  Layers,
  Users,
  FileText,
  Flag,
  Package,
  GitPullRequest,
  Undo2,
  Wrench,
  ScrollText,
} from 'lucide-react';

interface NavItem {
  to: string;
  label: string;
  icon: typeof Layers;
  description: string;
}

const NAV: NavItem[] = [
  {
    to: 'environments',
    label: 'Environments',
    icon: Layers,
    description: 'Environment keys and display names',
  },
  {
    to: 'roles',
    label: 'Participant Roles',
    icon: Users,
    description: 'Role dictionary used across deploys and promotions',
  },
  {
    to: 'activity-template',
    label: 'Activity Card Template',
    icon: FileText,
    description: 'Fields shown on deployment activity cards',
  },
  {
    to: 'feature-flags',
    label: 'Feature Flags',
    icon: Flag,
    description: 'Toggle platform features at runtime',
  },
  {
    to: 'catalog',
    label: 'Service Catalog',
    icon: Package,
    description: 'Catalog YAML source, sync and definitions',
  },
  {
    to: 'promotions',
    label: 'Promotions',
    icon: GitPullRequest,
    description: 'Approval policies and gating',
  },
  {
    to: 'rollbacks',
    label: 'Rollbacks',
    icon: Undo2,
    description: 'Choose which products can use rollbacks',
  },
  {
    to: 'maintenance',
    label: 'Maintenance',
    icon: Wrench,
    description: 'Data repair: duplicates, stranded promotions, log retention, webhook deliveries',
  },
  {
    to: 'release-notes-template',
    label: 'Release Notes Template',
    icon: ScrollText,
    description: 'Handlebars template applied to release notes',
  },
];

export function SettingsPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Settings
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Configure platform preferences
        </p>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-[220px_1fr] gap-4 lg:gap-6">
        {/* Left nav. Below `lg` it becomes a horizontally scrolling strip above the section — a
            220px column plus a settings form doesn't fit, and eleven stacked links would bury the
            section the user came to edit. */}
        <nav className="flex gap-1.5 overflow-x-auto pb-1 lg:block lg:space-y-0.5 lg:overflow-x-visible lg:pb-0">
          {NAV.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                `flex shrink-0 items-center gap-2.5 whitespace-nowrap rounded-lg px-3 py-2 text-[13px] transition-colors lg:shrink lg:whitespace-normal ${
                  isActive ? 'font-medium' : 'font-normal'
                }`
              }
              style={({ isActive }) => ({
                color: isActive ? 'var(--accent)' : 'var(--text-primary)',
                backgroundColor: isActive ? 'var(--accent-muted)' : 'transparent',
              })}
              title={item.description}
            >
              <item.icon size={14} />
              <span>{item.label}</span>
            </NavLink>
          ))}
        </nav>

        {/* Active section */}
        <div className="min-w-0">
          <Outlet />
        </div>
      </div>
    </div>
  );
}
