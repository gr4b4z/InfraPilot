import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { DeployEvent } from '@/lib/types';
import { formatDistanceToNow } from 'date-fns';

export interface EnvironmentConfig {
  key: string;
  displayName: string;
}

export interface ActivityTemplateLine {
  template: string;
  style: 'primary' | 'secondary' | 'muted';
}

interface SettingsState {
  /** Globally configured environments (shared across all products) */
  environments: EnvironmentConfig[];
  activityTemplate: ActivityTemplateLine[];

  setEnvironments: (envs: EnvironmentConfig[]) => void;
  setActivityTemplate: (lines: ActivityTemplateLine[]) => void;
  getDisplayName: (key: string) => string;
  getOrderedEnvironments: (keys: string[]) => string[];
}

const DEFAULT_ENVIRONMENTS: EnvironmentConfig[] = [
  { key: 'development', displayName: 'Development' },
  { key: 'staging', displayName: 'Staging' },
  { key: 'production', displayName: 'Production' },
];

export const DEFAULT_ACTIVITY_TEMPLATE: ActivityTemplateLine[] = [
  { template: '{ref:work-item:key} \u2014 {label:workItemTitle}', style: 'secondary' },
  { template: 'PR: {participant:PR Author}  \u00b7  QA: {participant:QA}  \u00b7  {time}', style: 'muted' },
];

export const useSettingsStore = create<SettingsState>()(
  persist(
    (set, get) => ({
      environments: DEFAULT_ENVIRONMENTS,
      activityTemplate: DEFAULT_ACTIVITY_TEMPLATE,

      setEnvironments: (envs) => set({ environments: envs }),

      setActivityTemplate: (lines) => set({ activityTemplate: lines }),

      getDisplayName: (key) => {
        const env = get().environments.find((e) => e.key === key);
        return env?.displayName ?? key;
      },

      getOrderedEnvironments: (keys) => {
        const order = get().environments.map((e) => e.key);
        return [...keys].sort((a, b) => {
          const ai = order.indexOf(a);
          const bi = order.indexOf(b);
          return (ai === -1 ? 999 : ai) - (bi === -1 ? 999 : bi);
        });
      },
    }),
    {
      name: 'platform-settings',
      // Migrate old shapes:
      //  v0: { environments }                            (pre-rename)
      //  v1: { defaultEnvironments, productEnvironments } (per-product overrides)
      //  v2: { environments }                            (current — global only)
      migrate: (persisted: unknown) => {
        const state = persisted as Record<string, unknown>;
        if (state.defaultEnvironments && !state.environments) {
          state.environments = state.defaultEnvironments;
        }
        delete state.defaultEnvironments;
        delete state.productEnvironments;
        return state as SettingsState;
      },
      version: 2,
    }
  )
);

/**
 * Resolve a template string against a DeployEvent.
 *
 * Placeholders:
 *   {service}, {environment}, {version}, {previousVersion}, {source}
 *   {label:<name>}          — enrichment label, e.g. {label:workItemTitle}
 *   {participant:<role>}    — participant displayName by role, e.g. {participant:PR Author}
 *   {ref:<type>:key}        — reference key by type, e.g. {ref:work-item:key}
 *   {ref:<type>:url}        — reference URL by type
 *   {time}                  — relative time ("2 hours ago")
 *
 * Returns null if ALL placeholders resolved to empty (line should be hidden).
 */
export function resolveTemplate(template: string, evt: DeployEvent): string | null {
  let hasValue = false;

  const result = template.replace(/\{([^}]+)\}/g, (_, expr: string) => {
    const value = resolvePlaceholder(expr.trim(), evt);
    if (value) hasValue = true;
    return value ?? '';
  });

  return hasValue ? result.replace(/\s{2,}/g, ' ').trim() : null;
}

function resolvePlaceholder(expr: string, evt: DeployEvent): string | null {
  // Simple fields
  if (expr === 'service') return evt.service;
  if (expr === 'environment') return evt.environment;
  if (expr === 'version') return evt.version;
  if (expr === 'previousVersion') return evt.previousVersion;
  if (expr === 'source') return evt.source;
  if (expr === 'time') return formatDistanceToNow(new Date(evt.deployedAt), { addSuffix: true });

  // {label:<name>}
  if (expr.startsWith('label:')) {
    const name = expr.slice(6);
    return evt.enrichment?.labels?.[name] ?? null;
  }

  // {participant:<role>}
  if (expr.startsWith('participant:')) {
    const role = expr.slice(12);
    const all = [...evt.participants, ...(evt.enrichment?.participants ?? [])];
    const p = all.find((x) => x.role === role);
    return p?.displayName ?? p?.email ?? null;
  }

  // {ref:<type>:key} or {ref:<type>:url}
  if (expr.startsWith('ref:')) {
    const parts = expr.slice(4).split(':');
    const type = parts.slice(0, -1).join(':'); // handles "work-item"
    const field = parts[parts.length - 1];
    const ref = evt.references.find((r) => r.type === type);
    if (!ref) return null;
    if (field === 'key') return ref.key ?? null;
    if (field === 'url') return ref.url ?? null;
    if (field === 'revision') return ref.revision ?? null;
    if (field === 'provider') return ref.provider ?? null;
    return null;
  }

  return null;
}
