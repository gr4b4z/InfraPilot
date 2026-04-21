import { useSettingsStore } from '@/stores/settingsStore';

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
