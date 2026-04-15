import { create } from 'zustand';
import { api } from '@/lib/api';
import type { ProductSummary, DeploymentStateEntry, DeployEvent } from '@/lib/types';

interface DeploymentState {
  products: ProductSummary[];
  stateMatrix: DeploymentStateEntry[];
  history: DeployEvent[];
  recentActivity: DeployEvent[];
  selectedProduct: string | null;
  selectedEnvironment: string | null;
  loading: boolean;

  fetchProducts: () => Promise<void>;
  fetchState: (product?: string, environment?: string) => Promise<void>;
  fetchHistory: (product: string, service: string, environment?: string, limit?: number) => Promise<void>;
  fetchRecent: (product: string, environment: string, since?: string) => Promise<void>;
  fetchRecentByProduct: (product: string, since: string) => Promise<void>;
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

  fetchProducts: async () => {
    set({ loading: true });
    try {
      const products = await api.getDeploymentProducts();
      set({ products });
    } finally {
      set({ loading: false });
    }
  },

  fetchState: async (product, environment) => {
    set({ loading: true });
    try {
      const stateMatrix = await api.getDeploymentState({ product, environment });
      set({ stateMatrix });
    } finally {
      set({ loading: false });
    }
  },

  fetchHistory: async (product, service, environment, limit) => {
    set({ loading: true });
    try {
      const history = await api.getDeploymentHistory(product, service, { environment, limit });
      set({ history });
    } finally {
      set({ loading: false });
    }
  },

  fetchRecent: async (product, environment, since) => {
    set({ loading: true });
    try {
      const history = await api.getRecentDeployments(product, environment, since);
      set({ history });
    } finally {
      set({ loading: false });
    }
  },

  fetchRecentByProduct: async (product, since) => {
    set({ loading: true });
    try {
      const recentActivity = await api.getRecentProductDeployments(product, since);
      set({ recentActivity });
    } finally {
      set({ loading: false });
    }
  },

  setSelectedProduct: (product) => set({ selectedProduct: product }),
  setSelectedEnvironment: (environment) => set({ selectedEnvironment: environment }),
}));
