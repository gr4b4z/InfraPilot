import { create } from 'zustand';
import { api } from '@/lib/api';

export const FeatureFlag = {
  Promotions: 'features.promotions',
  ServiceCatalog: 'features.serviceCatalog',
  Approvals: 'features.approvals',
} as const;

export type FeatureFlagKey = (typeof FeatureFlag)[keyof typeof FeatureFlag];

// Flags default to enabled on the client until the server response lands, so the
// shell doesn't flash a stripped-down nav on first paint. The seeder defaults
// ServiceCatalog and Approvals to true; Promotions is server-controlled.
const DEFAULT_ENABLED: Record<string, boolean> = {
  [FeatureFlag.Promotions]: true,
  [FeatureFlag.ServiceCatalog]: true,
  [FeatureFlag.Approvals]: true,
};

interface FeatureFlagsState {
  flags: Record<string, boolean>;
  loaded: boolean;
  load: () => Promise<void>;
  isEnabled: (key: string) => boolean;
}

export const useFeatureFlagsStore = create<FeatureFlagsState>((set, get) => ({
  flags: { ...DEFAULT_ENABLED },
  loaded: false,
  load: async () => {
    try {
      const { flags } = await api.listFeatureFlags();
      const map: Record<string, boolean> = { ...DEFAULT_ENABLED };
      for (const f of flags) map[f.key] = f.enabled;
      set({ flags: map, loaded: true });
    } catch {
      // Leave defaults in place; nav stays visible if the endpoint is unreachable.
      set({ loaded: true });
    }
  },
  isEnabled: (key) => {
    const map = get().flags;
    return map[key] ?? DEFAULT_ENABLED[key] ?? false;
  },
}));

export function useFeatureFlag(key: FeatureFlagKey): boolean {
  return useFeatureFlagsStore((s) => s.flags[key] ?? DEFAULT_ENABLED[key] ?? false);
}
