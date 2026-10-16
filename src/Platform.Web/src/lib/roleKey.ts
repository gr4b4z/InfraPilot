/**
 * Mirrors the server-side `RoleNormalizer` so a role string can be compared against the configured
 * vocabulary regardless of how it was typed or sent: lower-kebab-case, camelCase boundaries split,
 * everything else collapsed to single hyphens.
 *
 * Kept in its own leaf module (no imports) because both the settings store and the role helpers
 * that read that store need it.
 */
export function canonicaliseRoleKey(input: string | null | undefined): string {
  if (!input) return '';
  let s = input.trim();
  s = s.replace(/([a-z0-9])([A-Z])/g, '$1-$2'); // camelCase boundary
  s = s.toLowerCase();
  s = s.replace(/[\s_]+/g, '-');
  s = s.replace(/[^a-z0-9-]/g, '-');
  s = s.replace(/-+/g, '-').replace(/^-|-$/g, '');
  return s;
}

/** The slice of a configured role that alias resolution reads. Structural, to keep this module leaf. */
export interface RoleAliasSource {
  key: string;
  aliases?: string[] | null;
}

/**
 * Client mirror of the API's `RoleAliasMap.Resolve` (change them together): the canonical role a
 * role string means once the admin's aliases (Settings → Participant Roles) are applied — `qa` →
 * `qa-owner` when `qa` is listed as an alias of it — otherwise its own canonical form.
 *
 * Every key is matched before any alias, and earlier roles win an alias collision, matching the
 * server, so a settings row that slipped past validation still resolves the same on both sides.
 */
export function resolveRoleKey(input: string | null | undefined, roles: readonly RoleAliasSource[]): string {
  const canonical = canonicaliseRoleKey(input);
  if (!canonical) return canonical;
  for (const r of roles) {
    if (canonicaliseRoleKey(r.key) === canonical) return canonical;
  }
  for (const r of roles) {
    const key = canonicaliseRoleKey(r.key);
    if (key && (r.aliases ?? []).some((a) => canonicaliseRoleKey(a) === canonical)) return key;
  }
  return canonical;
}
