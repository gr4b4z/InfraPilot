import type { CSSProperties } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { envColorStyles } from '@/lib/envColor';

/**
 * Hooks for consuming an environment's colour directly, when a component needs the tokens
 * rather than a ready-made badge — a `<select>` that should wear the selected environment's
 * colour, a filter pill with its own active/inactive states, and so on.
 *
 * Kept out of `EnvBadge.tsx` so that file exports only components (react-refresh).
 */

/** Resolved colour tokens for an environment key. */
export function useEnvColor(env: string) {
  const color = useSettingsStore((s) => s.getEnvironmentColor(env));
  return envColorStyles(color);
}

/** Same, but tolerates "no environment" — returns null so callers can fall back to a
 *  generic accent (e.g. an "All environments" pill sitting among per-env ones). */
export function useEnvColorOrNull(env: string | null | undefined) {
  const color = useSettingsStore((s) => (env ? s.getEnvironmentColor(env) : null));
  return color ? envColorStyles(color) : null;
}

/**
 * Style for a form control that currently represents one environment — an env `<select>`
 * takes on that environment's colour, so an active env filter is visible without reading
 * the control. Falls back to neutral tokens when nothing is selected.
 */
export function useEnvControlStyle(env: string | null | undefined): CSSProperties {
  const color = useSettingsStore((s) => (env ? s.getEnvironmentColor(env) : null));
  if (!color) {
    return {
      borderColor: 'var(--border-color)',
      backgroundColor: 'var(--bg-primary)',
      color: 'var(--text-primary)',
    };
  }
  const { fg, bg, border } = envColorStyles(color);
  return { borderColor: border, backgroundColor: bg, color: fg };
}
