import { useMemo } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { canonicaliseRoleKey } from '@/lib/roleKey';

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
 * and deduped, in configured order. This is the set a person may be assigned to — the server
 * rejects anything else on the manual-assignment endpoints — so every role picker lists exactly
 * this, rather than whichever roles happen to appear in the data on screen.
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
    return out;
  }, [roles]);
}

/**
 * Predicate for "this role isn't in the configured vocabulary". Ingest records whatever role a
 * producer sends, so a work item can carry one the platform has no definition for: it can't be
 * labelled, reassigned, or reasoned about, so the UI flags it instead of rendering it as though it
 * were a normal slot.
 *
 * Returns false for everything until the settings have loaded — the store's first-paint value is a
 * built-in placeholder list, and marking against that would flag good roles for a frame.
 */
export function useIsUnrecognisedRole(): (role: string | null | undefined) => boolean {
  const configured = useConfiguredRoles();
  const loaded = useSettingsStore((s) => s.loaded);
  return useMemo(() => {
    const known = new Set(configured.map((r) => r.key));
    return (role: string | null | undefined) => {
      if (!loaded) return false;
      const key = canonicaliseRoleKey(role);
      return key.length > 0 && !known.has(key);
    };
  }, [configured, loaded]);
}
