import { useMemo } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { canonicaliseRoleKey, resolveRoleKey } from '@/lib/roleKey';

/**
 * Returns the display string for a participant role, using the admin-curated dictionary in
 * the settings store. Unmapped keys fall back to a humanised form of the canonical key.
 */
export function roleDisplay(
  participant: { role: string } | null | undefined,
): string {
  if (!participant) return '';
  return useSettingsStore.getState().getRoleDisplayName(participant.role);
}

/** One assignable role: the canonical key plus the label the admin gave it. */
export interface ConfiguredRole {
  key: string;
  displayName: string;
}

/**
 * The participant roles an admin has configured (Settings → Participant Roles), canonicalised
 * and deduped, **sorted by display name**. This is the set a person may be assigned to — the server
 * rejects anything else on the manual-assignment endpoints — so every role picker lists exactly
 * this, rather than whichever roles happen to appear in the data on screen.
 *
 * Sorted rather than left in configured order: the configured order is how an admin happened to
 * arrange a settings list, which tells a reader scanning a picker for "QA owner" nothing. Environment
 * pickers are the deliberate exception — there the configured order is the deployment order, which is
 * meaningful — and they use {@link useSettingsStore}'s `getOrderedEnvironments` instead.
 */
export function useConfiguredRoles(): ConfiguredRole[] {
  const roles = useSettingsStore((s) => s.roles);
  return useMemo(() => {
    const seen = new Set<string>();
    const out: ConfiguredRole[] = [];
    for (const r of roles) {
      const key = canonicaliseRoleKey(r.key);
      if (!key || seen.has(key)) continue;
      seen.add(key);
      out.push({ key, displayName: r.displayName?.trim() || key });
    }
    return out.sort((a, b) =>
      a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' }),
    );
  }, [roles]);
}

/**
 * Resolver for the canonical role a role string means — its configured role when it is that role's
 * key or one of its aliases (`qa` → `qa-owner`), otherwise its own canonical form. The client half of
 * the server's alias resolution: use it wherever two roles are compared.
 */
export function useResolveRoleKey(): (role: string | null | undefined) => string {
  const roles = useSettingsStore((s) => s.roles);
  return useMemo(() => (role: string | null | undefined) => resolveRoleKey(role, roles), [roles]);
}

/**
 * Predicate for "this role isn't in the configured vocabulary". Ingest records whatever role a
 * producer sends, so a work item can carry one the platform has no definition for: it can't be
 * labelled, reassigned, or reasoned about, so the UI flags it instead of rendering it as though it
 * were a normal slot. An alias of a configured role is that role, so it is never flagged.
 *
 * Returns false for everything until the settings have loaded — the store's first-paint value is a
 * built-in placeholder list, and marking against that would flag good roles for a frame.
 */
export function useIsUnrecognisedRole(): (role: string | null | undefined) => boolean {
  const configured = useConfiguredRoles();
  const resolve = useResolveRoleKey();
  const loaded = useSettingsStore((s) => s.loaded);
  return useMemo(() => {
    const known = new Set(configured.map((r) => r.key));
    return (role: string | null | undefined) => {
      if (!loaded) return false;
      const key = resolve(role);
      return key.length > 0 && !known.has(key);
    };
  }, [configured, resolve, loaded]);
}

/**
 * The directory groups the person picker narrows `role` to (alias-resolved), or an empty list when
 * anybody may be picked. Mirrors what `/promotions/users/search?role=` applies server-side, so the
 * picker can say who it is offering before the first keystroke.
 */
export function useRoleAssigneeGroups(role: string | null | undefined): { id: string; name: string }[] {
  const roles = useSettingsStore((s) => s.roles);
  return useMemo(() => {
    const key = resolveRoleKey(role, roles);
    if (!key) return [];
    const config = roles.find((r) => canonicaliseRoleKey(r.key) === key);
    return (config?.assigneeGroups ?? []).filter((g) => g.id?.trim());
  }, [role, roles]);
}
