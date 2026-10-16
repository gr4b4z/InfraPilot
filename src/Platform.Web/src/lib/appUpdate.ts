import { useEffect, useRef } from 'react';
import { useLocation, useNavigationType } from 'react-router-dom';
import { create } from 'zustand';
import { subscribeReconnect } from './realtime';

/**
 * Noticing that a newer release has been deployed while a tab is open.
 *
 * A tab keeps running the bundle it loaded, however many releases go out after it. That is how
 * "the new version needs a refresh" happens — the old code meets a new API, and pages break in ways
 * nobody can explain. The build publishes `/version.json` naming its entry bundle (see the
 * `buildInfo` plugin in vite.config.ts); when that stops matching the bundle this page loaded, a
 * newer release is live. The shell then offers a reload, and does it by itself on the next
 * navigation, when there is nothing on screen to lose.
 */

export interface BuildInfo {
  /** Release tag, `dev` outside a release build. */
  version: string;
  /** Path of the content-hashed entry script, e.g. `/assets/index-D5RzNg8s.js`. */
  entry: string;
}

interface AppUpdateState {
  /** The newer build that is live, or null while this page is current. */
  available: BuildInfo | null;
  /** Entry the user dismissed the banner for — a later release brings it back. */
  dismissedEntry: string | null;
  dismiss: () => void;
}

export const useAppUpdateStore = create<AppUpdateState>()((set, get) => ({
  available: null,
  dismissedEntry: null,
  dismiss: () => set({ dismissedEntry: get().available?.entry ?? null }),
}));

/**
 * The entry bundle this page is running, as index.html named it when the page loaded. Read from the
 * DOM rather than `import.meta.url`, which names whichever chunk this module ends up in.
 */
function readRunningEntry(): string | null {
  if (typeof document === 'undefined') return null;
  const script = document.querySelector<HTMLScriptElement>('script[type="module"][src]');
  if (!script) return null;
  try {
    return new URL(script.src, window.location.href).pathname;
  } catch {
    return null;
  }
}

export const runningEntry = readRunningEntry();

const CHECK_INTERVAL_MS = 5 * 60_000;
/** Tab focus can flap; this bounds the check to one per minute however often it does. */
const MIN_CHECK_GAP_MS = 60_000;
let lastCheck = 0;

export async function checkForUpdate(force = false): Promise<void> {
  if (!runningEntry) return;
  const now = Date.now();
  if (!force && now - lastCheck < MIN_CHECK_GAP_MS) return;
  lastCheck = now;

  let info: Partial<BuildInfo>;
  try {
    const response = await fetch('/version.json', { cache: 'no-store' });
    if (!response.ok) return;
    info = (await response.json()) as Partial<BuildInfo>;
  } catch {
    // Offline, or mid-deploy — the next check will tell.
    return;
  }
  if (typeof info.entry !== 'string' || !info.entry) return;

  if (info.entry === runningEntry) {
    // Back on the build we run (a rollback, or a replica that hadn't updated yet).
    if (useAppUpdateStore.getState().available) useAppUpdateStore.setState({ available: null });
    return;
  }
  if (useAppUpdateStore.getState().available?.entry === info.entry) return;
  useAppUpdateStore.setState({
    available: { version: typeof info.version === 'string' ? info.version : '', entry: info.entry },
  });
}

/**
 * Starts watching for new releases: every few minutes, whenever the tab comes back into view, and
 * whenever the realtime connection comes back — a deploy restarts the server, so that reconnect is
 * often the first sign of one. Off in dev, where Vite serves no version.json and HMR keeps the page
 * current anyway.
 */
export function startUpdateChecks(): () => void {
  if (import.meta.env.DEV || !runningEntry) return () => {};

  const interval = window.setInterval(() => void checkForUpdate(), CHECK_INTERVAL_MS);
  const onVisible = () => {
    if (document.visibilityState === 'visible') void checkForUpdate();
  };
  document.addEventListener('visibilitychange', onVisible);
  const unsubscribe = subscribeReconnect(() => void checkForUpdate(true));

  return () => {
    window.clearInterval(interval);
    document.removeEventListener('visibilitychange', onVisible);
    unsubscribe();
  };
}

const RELOADED_FOR_KEY = 'ip.reloadedForEntry';

/**
 * Reloads into the newer build, at most once per build per tab. If the reload still lands on the
 * old bundle — a stale cache or proxy in between — trying again on every navigation would turn
 * each click into a reload, so after one attempt it is left to the banner's button.
 */
export function reloadIntoUpdate(entry: string): boolean {
  try {
    if (window.sessionStorage.getItem(RELOADED_FOR_KEY) === entry) return false;
    window.sessionStorage.setItem(RELOADED_FOR_KEY, entry);
  } catch {
    // Storage blocked: without the guard, don't risk a loop.
    return false;
  }
  reloadApp();
  return true;
}

/** Reload past any stale cached page; with `reset`, clear this browser's saved app data first. */
export function reloadApp(reset = false): void {
  if (window.__ipBoot) window.__ipBoot.reload(reset);
  else window.location.reload();
}

/**
 * Mounted once in the shell: when a newer build is live, the next move to another page loads it
 * instead. Only real page changes count — PUSH (a link) or POP (back/forward) to a new pathname.
 * Filters rewrite the query string with REPLACE as you type, and a reload there would eat the input.
 */
export function useReloadIntoUpdateOnNavigate(): void {
  const { pathname } = useLocation();
  const navigationType = useNavigationType();
  const available = useAppUpdateStore((s) => s.available);
  const lastPathname = useRef(pathname);

  useEffect(() => {
    const moved = pathname !== lastPathname.current;
    lastPathname.current = pathname;
    if (!moved || !available || navigationType === 'REPLACE') return;
    reloadIntoUpdate(available.entry);
  }, [pathname, navigationType, available]);
}
