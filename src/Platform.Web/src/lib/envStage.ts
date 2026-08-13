import type { EnvironmentConfig } from '@/stores/settingsStore';

/**
 * Default pipeline-stage mapping for environment keys the settings don't know — the client
 * mirror of the API's `EnvironmentStage` (change them together). Producers send whatever their
 * pipelines call the environment; until an admin adds the key to Settings → Environments this
 * decides how it sorts and whether it reads as production. Explicit settings always win.
 */

/** dev-like < test-like < staging-like < unrecognised < prod-like. */
export function defaultStageRank(key: string): number {
  const k = key.trim().toLowerCase();
  if (k.startsWith('dev')) return 0;
  if (k.startsWith('test') || k.startsWith('qa') || k.startsWith('int')) return 1;
  if (k.startsWith('stag') || k.startsWith('uat') || k.startsWith('preprod') || k.startsWith('pre-prod')) return 2;
  if (isProductionByName(k)) return 4;
  return 3;
}

/** `prod` / `production` / `prd` / `live`, alone or suffixed (`prod-eu`); `preprod` never matches. */
export function isProductionByName(key: string): boolean {
  const k = key.trim().toLowerCase();
  for (const name of ['production', 'prod', 'prd', 'live']) {
    if (k === name) return true;
    if (k.startsWith(name) && '-_.'.includes(k[name.length] ?? '')) return true;
  }
  return false;
}

export type ProdSource = 'marked' | 'default-name' | 'convention';

/**
 * Resolves which of `universe` count as production stages, and where that answer came from:
 *   1. environments explicitly marked in settings ("marked");
 *   2. else unconfigured keys whose NAME reads as production ("default-name" — the fallback
 *      mapping that holds until an admin overrides it in settings);
 *   3. else the historical convention — the last environment in order ("convention").
 */
export function resolveProductionEnvs(
  universe: string[],
  configured: EnvironmentConfig[],
  ordered: string[],
): { envs: string[]; source: ProdSource } {
  const configuredKeys = new Set(configured.map((e) => e.key));
  const marked = universe.filter((k) =>
    configured.some((e) => e.key === k && e.isProduction),
  );
  if (marked.length > 0) return { envs: orderLike(marked, ordered), source: 'marked' };

  const byName = universe.filter((k) => !configuredKeys.has(k) && isProductionByName(k));
  if (byName.length > 0) return { envs: orderLike(byName, ordered), source: 'default-name' };

  const last = ordered[ordered.length - 1];
  return { envs: last ? [last] : [], source: 'convention' };
}

function orderLike(keys: string[], ordered: string[]): string[] {
  return [...keys].sort((a, b) => ordered.indexOf(a) - ordered.indexOf(b));
}
