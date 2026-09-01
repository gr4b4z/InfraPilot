import {
  LayoutGrid,
  FileText,
  CheckCircle,
  ChartColumn,
  GitPullRequest,
  History,
  Inbox,
  Rocket,
  ScrollText,
  Settings,
  Undo2,
  Webhook,
} from 'lucide-react';
import { FeatureFlag } from '@/stores/featureFlagsStore';

/**
 * Where `:` can take you.
 *
 * One list drives both the palette and the help overlay, so an accelerator can't exist without being
 * shown. `key` is the letter typed after `:` — kept unique here, since the palette dispatches on it
 * directly rather than on position.
 *
 * Feature flags and admin-only entries are filtered by the palette against the same stores the
 * sidebar uses, so the palette never offers a destination the nav doesn't have.
 */
export interface NavTarget {
  key: string;
  label: string;
  to: string;
  icon: React.ComponentType<{ size?: number; className?: string }>;
  featureFlag?: string;
  adminOnly?: boolean;
  /** Visible to QA and Admins only — the roles tasks can actually be assigned to. */
  qaOnly?: boolean;
}

export const NAV_TARGETS: NavTarget[] = [
  { key: 'd', label: 'Deployments', to: '/deployments', icon: Rocket },
  { key: 'y', label: 'Analytics', to: '/analytics', icon: ChartColumn, featureFlag: FeatureFlag.Analytics },
  { key: 'p', label: 'Promotions', to: '/promotions', icon: GitPullRequest, featureFlag: FeatureFlag.Promotions },
  { key: 'w', label: 'Work items queue', to: '/me/work-items', icon: Inbox, featureFlag: FeatureFlag.Promotions },
  { key: 'u', label: 'Promotions audit', to: '/promotions/audit', icon: History, featureFlag: FeatureFlag.Promotions },
  { key: 't', label: 'My tasks', to: '/my-tasks', icon: CheckCircle, qaOnly: true },
  { key: 'c', label: 'Service catalog', to: '/catalog', icon: LayoutGrid, featureFlag: FeatureFlag.ServiceCatalog },
  { key: 'q', label: 'My requests', to: '/requests', icon: FileText, featureFlag: FeatureFlag.ServiceCatalog },
  { key: 'a', label: 'Approvals', to: '/approvals', icon: CheckCircle, featureFlag: FeatureFlag.Approvals },
  { key: 'r', label: 'Rollbacks', to: '/rollbacks', icon: Undo2, featureFlag: FeatureFlag.Rollbacks },
  { key: 'n', label: 'Release notes', to: '/release-notes', icon: ScrollText, featureFlag: FeatureFlag.ReleaseNotes },
  { key: 'h', label: 'Webhooks', to: '/webhooks', icon: Webhook, adminOnly: true },
  { key: 's', label: 'Settings', to: '/settings', icon: Settings, adminOnly: true },
];
