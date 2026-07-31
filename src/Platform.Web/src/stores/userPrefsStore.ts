import { create } from 'zustand';
import { api } from '@/lib/api';
import { useDeploymentStore } from '@/stores/deploymentStore';
import { refreshMyTasks } from '@/stores/myTasksStore';

/**
 * The signed-in user's own preferences, held server-side so they follow the person rather than the
 * browser.
 *
 * <p>The hidden-product set is enforced by the API — every product-scoped list already comes back
 * filtered — so nothing here does any filtering. What the store is for is (a) rendering the control
 * and (b) telling the rest of the app when the answer changed, because a server-side filter means
 * every already-loaded list is stale the moment the set is saved.</p>
 */
interface UserPrefsState {
  hiddenProducts: string[];
  /** False until the first load resolves. Consumers that count on the set must wait for it. */
  loaded: boolean;
  saving: boolean;
  load: () => Promise<void>;
  setHiddenProducts: (products: string[]) => Promise<void>;
}

export const useUserPrefsStore = create<UserPrefsState>((set) => ({
  hiddenProducts: [],
  loaded: false,
  saving: false,

  load: async () => {
    try {
      const { hiddenProducts } = await api.getMyPreferences();
      set({ hiddenProducts, loaded: true });
    } catch {
      // Show everything rather than nothing: an unreachable preferences endpoint must not leave the
      // user staring at a filtered app they can't account for.
      set({ hiddenProducts: [], loaded: true });
    }
  },

  setHiddenProducts: async (products) => {
    set({ saving: true });
    try {
      const { hiddenProducts } = await api.setMyHiddenProducts(products);
      set({ hiddenProducts, saving: false });

      // The filter lives on the server, so everything already on screen is now wrong. These two
      // stores are the ones with app-wide reach — the product matrix, and the counts behind the
      // sidebar and bell badges. Page-local lists refetch when they next mount.
      await Promise.all([
        useDeploymentStore.getState().fetchProducts(),
        refreshMyTasks(),
      ]);
    } catch (err) {
      set({ saving: false });
      throw err;
    }
  },
}));

/** How many products the user is hiding. Drives the "you are not seeing everything" cue. */
export function useHiddenProductCount(): number {
  return useUserPrefsStore((s) => s.hiddenProducts.length);
}
