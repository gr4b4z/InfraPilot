import { useCallback, useSyncExternalStore } from 'react';

/**
 * Subscribes a component to a CSS media query.
 *
 * Anything that is purely a matter of styling belongs in Tailwind's breakpoint prefixes, not here —
 * this hook is for the handful of places where a breakpoint changes *behaviour* rather than looks:
 * the chat panel taking over the content area instead of sitting beside it, the nav being a modal
 * drawer instead of a rail. Those need the branch in JS because they change which elements render
 * and which side effects (focus handling, auto-focus) apply.
 *
 * `useSyncExternalStore` rather than `useState` + `useEffect`: `matchMedia` is exactly the external
 * store it exists for, and reading through it avoids both the first-paint flash at the wrong
 * breakpoint and the cascading render a setState-in-effect subscription causes.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const mediaQuery = window.matchMedia(query);
      mediaQuery.addEventListener('change', onStoreChange);
      return () => mediaQuery.removeEventListener('change', onStoreChange);
    },
    [query],
  );

  const getSnapshot = useCallback(() => window.matchMedia(query).matches, [query]);

  // No window to measure during prerender — report "not matching" so the mobile-first markup is
  // what hydrates, then the first client snapshot corrects it.
  const getServerSnapshot = useCallback(() => false, []);

  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

/**
 * Tailwind's `lg` breakpoint — the width at which the shell has room for a permanent left sidebar
 * plus a 380px chat panel alongside the content. Keep in step with the `lg:` prefixes in
 * {@link Layout}, {@link Sidebar}, {@link Topbar} and {@link ChatSidebar}.
 */
export const DESKTOP_QUERY = '(min-width: 1024px)';

/** True when the shell is wide enough for the side-by-side desktop layout. */
export function useIsDesktop(): boolean {
  return useMediaQuery(DESKTOP_QUERY);
}
