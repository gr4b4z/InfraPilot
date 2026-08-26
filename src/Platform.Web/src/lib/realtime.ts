import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { acquireToken, isMsalEnabled } from './auth';
import { isLocalAuthEnabled } from './authConfig';
import { getStoredToken } from './localAuth';
import { buildApiUrl } from './runtimeConfig';

/**
 * The compact "this entity changed" signal the API broadcasts after every mutation.
 * Mirrors Platform.Api's EntityChangedEvent. Pages don't get the changed data itself —
 * they refetch through the normal authorized endpoints.
 */
export interface EntityChangedEvent {
  entity: string;
  action: string;
  id?: string | null;
  key?: string | null;
  product?: string | null;
  environment?: string | null;
  timestamp?: string;
}

/** Human-readable notification (chat sidebar), mirrors Platform.Api's PlatformEvent. */
export interface PlatformNotification {
  type: string;
  requestId?: string;
  serviceName?: string;
  oldStatus?: string;
  newStatus?: string;
  actorName?: string;
  message?: string;
  timestamp?: string;
}

type EntityHandler = (evt: EntityChangedEvent) => void;
type NotificationHandler = (evt: PlatformNotification) => void;
type ReconnectHandler = () => void;

const entityHandlers = new Set<EntityHandler>();
const notificationHandlers = new Set<NotificationHandler>();
const reconnectHandlers = new Set<ReconnectHandler>();

let connection: HubConnection | null = null;
let startTimer: ReturnType<typeof setTimeout> | undefined;

async function tokenFactory(): Promise<string> {
  if (isMsalEnabled()) return (await acquireToken()) ?? '';
  if (isLocalAuthEnabled()) return getStoredToken() ?? '';
  return '';
}

/**
 * Opens the hub connection (idempotent — subsequent calls are no-ops while one exists).
 * Reconnects forever with capped backoff: this is a passive channel, so staying subscribed
 * costs nothing and giving up would silently freeze every page until a manual reload.
 */
export function startRealtime(): void {
  if (connection) return;

  connection = new HubConnectionBuilder()
    .withUrl(buildApiUrl('/hubs/events'), { accessTokenFactory: tokenFactory })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) =>
        Math.min(30_000, 1000 * 2 ** ctx.previousRetryCount),
    })
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on('entityChanged', (evt: EntityChangedEvent) => {
    for (const handler of entityHandlers) handler(evt);
  });

  connection.on('notification', (evt: PlatformNotification) => {
    for (const handler of notificationHandlers) handler(evt);
  });

  // Automatic reconnect resumes the transport but events sent meanwhile are gone —
  // subscribers treat this as "anything may have changed" and refetch.
  connection.onreconnected(() => {
    for (const handler of reconnectHandlers) handler();
  });

  void tryStart();
}

async function tryStart(delayMs = 1000, isRetry = false): Promise<void> {
  const conn = connection;
  if (!conn || conn.state !== HubConnectionState.Disconnected) return;
  try {
    await conn.start();
    // If connecting took retries, the app has been running blind — page loads may have failed
    // against a down API and events were missed either way, so treat success like a reconnect.
    // A clean first connect skips this: pages are fetching fresh data at mount already.
    if (isRetry) for (const handler of reconnectHandlers) handler();
  } catch {
    // withAutomaticReconnect only covers an established connection dropping;
    // initial-start failures (API restarting, token hiccup) retry here.
    startTimer = setTimeout(() => void tryStart(Math.min(30_000, delayMs * 2), true), delayMs);
  }
}

export function stopRealtime(): void {
  if (startTimer !== undefined) clearTimeout(startTimer);
  startTimer = undefined;
  const conn = connection;
  connection = null;
  if (conn) void conn.stop();
}

export function subscribeEntityEvents(handler: EntityHandler): () => void {
  entityHandlers.add(handler);
  return () => entityHandlers.delete(handler);
}

export function subscribeNotifications(handler: NotificationHandler): () => void {
  notificationHandlers.add(handler);
  return () => notificationHandlers.delete(handler);
}

export function subscribeReconnect(handler: ReconnectHandler): () => void {
  reconnectHandlers.add(handler);
  return () => reconnectHandlers.delete(handler);
}
