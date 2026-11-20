import { create } from 'zustand';
import { buildApiUrl } from './runtimeConfig';

/**
 * Why the app couldn't talk to the API, in terms a user can pass on. The point is the difference
 * between these — "my network is down", "the server is restarting" and "something in between is
 * rewriting responses" all used to surface as the same empty page.
 */
export type ConnectionProblemKind =
  /** The browser itself reports no network. */
  | 'offline'
  /** The request never got an HTTP answer: DNS, VPN, proxy, TLS, or nothing listening. */
  | 'network'
  /** Sent, but no answer within {@link REQUEST_TIMEOUT_MS}. */
  | 'timeout'
  /** 502/503/504 — the web tier answered for an API that didn't. */
  | 'unavailable'
  /** Any other non-2xx where a 2xx was the only sane answer. */
  | 'http-error'
  /** A 2xx that isn't JSON — usually a proxy or sign-in page answering in the API's place. */
  | 'unexpected-response';

export interface ConnectionProblem {
  kind: ConnectionProblemKind;
  /** What was being fetched — a same-origin path, or the full URL when the API is elsewhere. */
  url: string;
  status?: number;
  /** The browser's own wording (exception message, content type), for the diagnostics. */
  detail?: string;
  /** ISO instant the problem was seen. */
  at: string;
}

export const REQUEST_TIMEOUT_MS = 15_000;

/** The statuses that mean "the API isn't there", as opposed to "the API said no". */
export const GATEWAY_STATUSES = new Set([502, 503, 504]);

function displayUrl(url: string): string {
  try {
    const parsed = new URL(url, window.location.href);
    return parsed.origin === window.location.origin ? parsed.pathname : parsed.origin + parsed.pathname;
  } catch {
    return url;
  }
}

/** A fetch that threw rather than returning a response. */
export function networkProblem(url: string, error: unknown, timedOut = false): ConnectionProblem {
  const kind: ConnectionProblemKind = timedOut ? 'timeout' : navigator.onLine ? 'network' : 'offline';
  return {
    kind,
    url: displayUrl(url),
    detail: error instanceof Error ? error.message : String(error),
    at: new Date().toISOString(),
  };
}

export function httpProblem(url: string, response: Response): ConnectionProblem {
  return {
    kind: GATEWAY_STATUSES.has(response.status) ? 'unavailable' : 'http-error',
    url: displayUrl(url),
    status: response.status,
    detail: response.statusText || undefined,
    at: new Date().toISOString(),
  };
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

export type JsonResult<T> = { ok: true; data: T } | { ok: false; problem: ConnectionProblem };

/**
 * GET-and-parse that reports *why* it failed instead of throwing. For the requests the app can't
 * start without, where "it didn't work" is not enough to act on.
 */
export async function fetchJson<T>(url: string, init: RequestInit = {}): Promise<JsonResult<T>> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  let response: Response;
  try {
    response = await fetch(url, { ...init, signal: controller.signal });
  } catch (error) {
    return { ok: false, problem: networkProblem(url, error, controller.signal.aborted) };
  } finally {
    window.clearTimeout(timer);
  }

  if (!response.ok) return { ok: false, problem: httpProblem(url, response) };

  try {
    return { ok: true, data: (await response.json()) as T };
  } catch {
    return {
      ok: false,
      problem: {
        kind: 'unexpected-response',
        url: displayUrl(url),
        status: response.status,
        detail: `Expected JSON, got ${response.headers.get('content-type') || 'no content type'}`,
        at: new Date().toISOString(),
      },
    };
  }
}

/** Headline and explanation for a problem, written for the person looking at the screen. */
export function describeProblem(problem: ConnectionProblem): { title: string; message: string } {
  switch (problem.kind) {
    case 'offline':
      return {
        title: 'You appear to be offline',
        message: 'Your browser reports no network connection. This page reconnects by itself once you are back online.',
      };
    case 'network':
      return {
        title: 'Can’t reach the server',
        message:
          'The request failed before the server answered. Check your network or VPN connection — or the server may be restarting.',
      };
    case 'timeout':
      return {
        title: 'The server isn’t responding',
        message: `No answer within ${REQUEST_TIMEOUT_MS / 1000} seconds. The server may be overloaded or restarting.`,
      };
    case 'unavailable':
      return {
        title: `The server is unavailable (HTTP ${problem.status})`,
        message:
          'The web server is up, but the API behind it didn’t answer — it is probably restarting or overloaded. This usually clears within a minute.',
      };
    case 'http-error':
      return {
        title: `The server returned an error (HTTP ${problem.status})`,
        message: 'The API answered, but not with what the app needs to start.',
      };
    case 'unexpected-response':
      return {
        title: 'Unexpected response from the server',
        message:
          'The server answered with something other than the API’s data — often a proxy, firewall or sign-in page in between.',
      };
  }
}

/** One problem as diagnostics lines. */
export function problemLines(problem: ConnectionProblem): string[] {
  return [
    `Problem: ${problem.kind}${problem.status ? ` (HTTP ${problem.status})` : ''}`,
    ...(problem.url ? [`Request: ${problem.url}`] : []),
    ...(problem.detail ? [`Detail: ${problem.detail}`] : []),
    `Seen at: ${problem.at}`,
  ];
}

// ── Runtime connection state ───────────────────────────────────────────────────────────────

interface ConnectionState {
  /** The most recent unresolved problem; null while the API is answering. */
  problem: ConnectionProblem | null;
}

/**
 * Whether the API is reachable right now, fed by every API call (see `ApiClient.request`) and by
 * the browser's online/offline events. Drives the connection banner in the shell.
 */
export const useConnectionStore = create<ConnectionState>()(() => ({ problem: null }));

const PROBE_DELAYS_MS = [3_000, 5_000, 10_000, 20_000, 30_000];
let probeTimer: number | undefined;
let probeAttempt = 0;

/** Anonymous and database-free: answers whenever the API process is up. */
const probeUrl = () => buildApiUrl('/auth/config');

async function probe(): Promise<void> {
  probeTimer = undefined;
  const result = await fetchJson(probeUrl(), { cache: 'no-store' });
  if (result.ok) {
    reportConnectionHealthy();
    return;
  }
  useConnectionStore.setState({ problem: result.problem });
  scheduleProbe();
}

function scheduleProbe(delay = PROBE_DELAYS_MS[Math.min(probeAttempt, PROBE_DELAYS_MS.length - 1)]) {
  if (probeTimer !== undefined) return;
  probeAttempt += 1;
  probeTimer = window.setTimeout(() => void probe(), delay);
}

/**
 * An API call couldn't get an answer. Shows the banner and keeps probing in the background until
 * the API answers again — so the banner clears by itself even if the page makes no further calls.
 */
export function reportConnectionProblem(problem: ConnectionProblem): void {
  useConnectionStore.setState({ problem });
  scheduleProbe();
}

/** Any HTTP answer from the API proves it is reachable. Cheap when nothing is wrong. */
export function reportConnectionHealthy(): void {
  if (probeTimer !== undefined) window.clearTimeout(probeTimer);
  probeTimer = undefined;
  probeAttempt = 0;
  if (useConnectionStore.getState().problem) useConnectionStore.setState({ problem: null });
}

/** Probe right away rather than waiting out the backoff — the banner's "Retry now". */
export function retryConnectionNow(): void {
  if (probeTimer !== undefined) window.clearTimeout(probeTimer);
  probeTimer = undefined;
  void probe();
}

if (typeof window !== 'undefined') {
  window.addEventListener('offline', () =>
    reportConnectionProblem({ kind: 'offline', url: '', at: new Date().toISOString() }),
  );
  window.addEventListener('online', () => {
    if (useConnectionStore.getState().problem) retryConnectionNow();
  });
}
