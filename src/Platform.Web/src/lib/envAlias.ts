import { canonicaliseRoleKey } from '@/lib/roleKey';
import type { EnvironmentConfig } from '@/stores/settingsStore';

/**
 * Client mirror of the API's `EnvironmentAliasMap` (change them together).
 *
 * Producers name the same physical environment whatever their pipeline calls it — "dev",
 * "develop", "development". An admin lists the variants as aliases on the one real environment and
 * the server resolves every write through them, so new traffic converges on the canonical key. The
 * rows that arrived *before* that still carry the old names until an admin merges them, and this is
 * what lets the UI label and colour those rows as the environment they actually are in the
 * meantime.
 *
 * Matching is three-tier, narrowest first: exact, case-insensitive, then the lower-kebab form
 * (`canonicaliseRoleKey`, the mirror of the server's `RoleNormalizer`) so "Pre Prod", "pre_prod"
 * and "preProd" all reach an alias written "pre-prod". Anything unconfigured passes through
 * unchanged — an environment nobody has curated yet must keep working.
 */
export function resolveEnvKey(key: string, environments: EnvironmentConfig[]): string {
  const name = (key ?? '').trim();
  if (!name) return name;

  // Earlier entries win a collision, matching the server, so an ambiguous settings row (hand-edited
  // JSON, or one written before the validation existed) resolves the same way on both sides.
  for (const env of environments) {
    if (env.key.trim() === name) return env.key.trim();
  }
  const lowered = name.toLowerCase();
  for (const env of environments) {
    if (env.key.trim().toLowerCase() === lowered) return env.key.trim();
    if ((env.aliases ?? []).some((a) => (a ?? '').trim().toLowerCase() === lowered)) return env.key.trim();
  }
  const canonical = canonicaliseRoleKey(name);
  if (canonical) {
    for (const env of environments) {
      if (canonicaliseRoleKey(env.key) === canonical) return env.key.trim();
      if ((env.aliases ?? []).some((a) => canonicaliseRoleKey(a) === canonical)) return env.key.trim();
    }
  }
  return name;
}
