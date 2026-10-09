import { runningEntry, useAppUpdateStore } from './appUpdate';
import { getAppName } from './runtimeConfig';

/**
 * The "Copy details" text on every error screen: enough for whoever picks up the report to tell a
 * stale bundle from a down API from a bug, without a screen share. Nothing secret — no tokens, no
 * storage contents — since it is meant to be pasted into a ticket or a chat.
 */
export function diagnosticsReport(heading: string, lines: string[] = []): string {
  const update = useAppUpdateStore.getState().available;
  return [
    `${getAppName()} — ${heading}`,
    `Time: ${new Date().toISOString()}`,
    `Page: ${window.location.href}`,
    `Build: ${__APP_VERSION__} ${runningEntry ?? '(unknown bundle)'}`,
    ...(update ? [`Newer build live: ${update.version || '?'} ${update.entry}`] : []),
    `Online: ${navigator.onLine ? 'yes' : 'no'}`,
    `Browser: ${navigator.userAgent}`,
    ...(lines.length ? ['', ...lines] : []),
  ].join('\n');
}

/** Lines for a caught error: message, then as much of the stack as is useful. */
export function errorLines(error: unknown, componentStack?: string | null): string[] {
  const lines =
    error instanceof Error
      ? [`Error: ${error.name}: ${error.message}`, ...(error.stack ? ['', error.stack] : [])]
      : [`Error: ${String(error)}`];
  if (componentStack) lines.push('', `Component stack:${componentStack}`);
  return lines;
}

/** What "Reset saved data and reload" costs, for the screens that offer it. */
export const RESET_FOOTNOTE =
  '“Reset saved data” signs you out and clears saved filters and the assistant conversation in this browser.';

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
