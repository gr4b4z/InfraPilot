import { create } from 'zustand';

/**
 * Shell chrome state that more than one component needs to read.
 *
 * The rail collapse used to be local to {@link Sidebar}, but the narrow-screen drawer is opened
 * from the topbar's hamburger and closed from inside the sidebar, so the two flags now live here
 * together. Neither is persisted: the drawer is a transient overlay, and the rail collapse is a
 * per-session choice on a viewport wide enough that reopening it costs one click.
 */
interface UiState {
  /**
   * Off-canvas nav drawer. Only meaningful below `lg`, where the sidebar is a modal overlay;
   * at `lg` and up the sidebar is always present and this is ignored.
   */
  navDrawerOpen: boolean;
  /** Icons-only rail. Only meaningful at `lg` and up — the drawer is always full width. */
  navCollapsed: boolean;

  setNavDrawerOpen: (open: boolean) => void;
  toggleNavDrawer: () => void;
  toggleNavCollapsed: () => void;
}

export const useUiStore = create<UiState>()((set) => ({
  navDrawerOpen: false,
  navCollapsed: false,

  setNavDrawerOpen: (open) => set({ navDrawerOpen: open }),
  toggleNavDrawer: () => set((state) => ({ navDrawerOpen: !state.navDrawerOpen })),
  toggleNavCollapsed: () => set((state) => ({ navCollapsed: !state.navCollapsed })),
}));
