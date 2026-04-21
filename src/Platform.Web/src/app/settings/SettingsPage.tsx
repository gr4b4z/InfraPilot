import { NavLink, Outlet } from 'react-router-dom';
import {
  Layers,
  Users,
  FileText,
  Flag,
  Package,
  GitPullRequest,
  Wrench,
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
    description: 'Approver groups, policies, and topology',
  },
  {
    to: 'deployment-maintenance',
    label: 'Deployment Maintenance',
    icon: Wrench,
    description: 'Clean up duplicate deployment events',
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

      <div className="grid grid-cols-[220px_1fr] gap-6">
        {/* Left nav */}
        <nav className="space-y-0.5">
          {NAV.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                `flex items-center gap-2.5 px-3 py-2 rounded-lg text-[13px] transition-colors ${
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
