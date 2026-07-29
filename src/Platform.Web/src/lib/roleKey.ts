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
