import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { DeployEvent } from '@/lib/types';
import { formatDistanceToNow } from 'date-fns';

export interface EnvironmentConfig {
  key: string;
  displayName: string;
}

export interface ProductEnvironments {
  product: string;
  environments: EnvironmentConfig[];
}

export interface ActivityTemplateLine {
  template: string;
  style: 'primary' | 'secondary' | 'muted';
}

interface SettingsState {
  /** Default environments used when a product has no specific config */
  defaultEnvironments: EnvironmentConfig[];
  /** Per-product environment overrides */
  productEnvironments: ProductEnvironments[];
  activityTemplate: ActivityTemplateLine[];

  setDefaultEnvironments: (envs: EnvironmentConfig[]) => void;
  setProductEnvironments: (product: string, envs: EnvironmentConfig[]) => void;
  removeProductEnvironments: (product: string) => void;
  setActivityTemplate: (lines: ActivityTemplateLine[]) => void;
  /** Get the environment config list for a product (falls back to defaults) */
  getEnvironments: (product?: string) => EnvironmentConfig[];
  getDisplayName: (key: string, product?: string) => string;
  getOrderedEnvironments: (keys: string[], product?: string) => string[];
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
      defaultEnvironments: DEFAULT_ENVIRONMENTS,
      productEnvironments: [],
      activityTemplate: DEFAULT_ACTIVITY_TEMPLATE,

      setDefaultEnvironments: (envs) => set({ defaultEnvironments: envs }),

      setProductEnvironments: (product, envs) =>
        set((state) => {
          const existing = state.productEnvironments.filter((pe) => pe.product !== product);
          return { productEnvironments: [...existing, { product, environments: envs }] };
        }),

      removeProductEnvironments: (product) =>
        set((state) => ({
          productEnvironments: state.productEnvironments.filter((pe) => pe.product !== product),
        })),

      setActivityTemplate: (lines) => set({ activityTemplate: lines }),

      getEnvironments: (product) => {
        if (product) {
          const pe = get().productEnvironments.find((p) => p.product === product);
          if (pe) return pe.environments;
        }
        return get().defaultEnvironments;
      },

      getDisplayName: (key, product) => {
        const envs = get().getEnvironments(product);
        const env = envs.find((e) => e.key === key);
        if (env) return env.displayName;
        // Fallback: check defaults too in case product config omits this env
        const def = get().defaultEnvironments.find((e) => e.key === key);
        return def?.displayName ?? key;
      },

      getOrderedEnvironments: (keys, product) => {
        const envs = get().getEnvironments(product);
        const order = envs.map((e) => e.key);
        return [...keys].sort((a, b) => {
          const ai = order.indexOf(a);
          const bi = order.indexOf(b);
          return (ai === -1 ? 999 : ai) - (bi === -1 ? 999 : bi);
        });
      },
    }),
    {
      name: 'platform-settings',
      // Migrate old shape: `environments` → `defaultEnvironments`
      migrate: (persisted: unknown) => {
        const state = persisted as Record<string, unknown>;
        if (state.environments && !state.defaultEnvironments) {
          state.defaultEnvironments = state.environments;
          delete state.environments;
        }
        if (!state.productEnvironments) {
          state.productEnvironments = [];
        }
        return state as SettingsState;
      },
      version: 1,
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
