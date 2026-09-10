import { create } from 'zustand';
import { api } from '@/lib/api';
import type { ProductSummary, DeploymentStateEntry, DeployEvent } from '@/lib/types';

/**
 * `silent` leaves `loading` alone, so the page keeps what it is showing until the fresh data swaps
 * in. Pages pass it for realtime refreshes of a query already on screen; a new query (mount, a
 * different product or filter) fetches loudly so the stale data is never shown under a new heading.
 */
export interface FetchOptions {
  silent?: boolean;
}

interface DeploymentState {
  products: ProductSummary[];
  stateMatrix: DeploymentStateEntry[];
  history: DeployEvent[];
  recentActivity: DeployEvent[];
  selectedProduct: string | null;
  selectedEnvironment: string | null;
  loading: boolean;

  fetchProducts: (opts?: FetchOptions) => Promise<void>;
  fetchState: (product?: string, environment?: string, opts?: FetchOptions) => Promise<void>;
  fetchHistory: (
    product: string,
    service: string,
    environment?: string,
    limit?: number,
    opts?: FetchOptions,
  ) => Promise<void>;
  fetchRecent: (product: string, environment: string, since?: string, opts?: FetchOptions) => Promise<void>;
  fetchRecentByProduct: (product: string, since: string, opts?: FetchOptions) => Promise<void>;
  setSelectedProduct: (product: string | null) => void;
  setSelectedEnvironment: (environment: string | null) => void;
}

export const useDeploymentStore = create<DeploymentState>((set) => ({
  products: [],
  stateMatrix: [],
  history: [],
  recentActivity: [],
  selectedProduct: null,
  selectedEnvironment: null,
  loading: false,

  fetchProducts: async (opts) => {
    if (!opts?.silent) set({ loading: true });
    try {
      const products = await api.getDeploymentProducts();
      set({ products });
    } finally {
      if (!opts?.silent) set({ loading: false });
    }
  },

  fetchState: async (product, environment, opts) => {
    if (!opts?.silent) set({ loading: true });
    try {
      const stateMatrix = await api.getDeploymentState({ product, environment });
      set({ stateMatrix });
    } finally {
      if (!opts?.silent) set({ loading: false });
    }
  },

  fetchHistory: async (product, service, environment, limit, opts) => {
    if (!opts?.silent) set({ loading: true });
    try {
      const history = await api.getDeploymentHistory(product, service, { environment, limit });
      set({ history });
    } finally {
      if (!opts?.silent) set({ loading: false });
    }
  },

  fetchRecent: async (product, environment, since, opts) => {
    if (!opts?.silent) set({ loading: true });
    try {
      const history = await api.getRecentDeployments(product, environment, since);
      set({ history });
    } finally {
      if (!opts?.silent) set({ loading: false });
    }
  },

  fetchRecentByProduct: async (product, since, opts) => {
    if (!opts?.silent) set({ loading: true });
    try {
      const recentActivity = await api.getRecentProductDeployments(product, since);
      set({ recentActivity });
    } finally {
      if (!opts?.silent) set({ loading: false });
    }
  },

  setSelectedProduct: (product) => set({ selectedProduct: product }),
  setSelectedEnvironment: (environment) => set({ selectedEnvironment: environment }),
}));
