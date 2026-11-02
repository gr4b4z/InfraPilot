import type { MigrationFlag, MigrationItemStatus, MigrationKind, MigrationRunStatus } from '@/lib/types';

/** Colours and words for a migration run's status — one table, so the page, the card and the flare agree. */
export const MIGRATION_STATUS: Record<MigrationRunStatus, { bg: string; fg: string; label: string }> = {
  pending: { bg: 'var(--warning-bg)', fg: 'var(--warning)', label: 'Pending' },
  running: { bg: 'var(--info-bg)', fg: 'var(--info)', label: 'Running' },
  succeeded: { bg: 'var(--success-bg)', fg: 'var(--success)', label: 'Succeeded' },
  failed: { bg: 'var(--danger-bg)', fg: 'var(--danger)', label: 'Failed' },
  skipped: { bg: 'var(--bg-secondary)', fg: 'var(--text-muted)', label: 'Skipped' },
  superseded: { bg: 'var(--bg-secondary)', fg: 'var(--text-muted)', label: 'Superseded' },
};

export const MIGRATION_ITEM_STATUS: Record<MigrationItemStatus, { fg: string; label: string; mark: string }> = {
  applied: { fg: 'var(--success)', label: 'Applied', mark: '✓' },
  failed: { fg: 'var(--danger)', label: 'Failed', mark: '✗' },
  'rolled-back': { fg: 'var(--warning)', label: 'Rolled back', mark: '↶' },
  running: { fg: 'var(--info)', label: 'Running', mark: '…' },
  pending: { fg: 'var(--text-muted)', label: 'Not run', mark: '·' },
  skipped: { fg: 'var(--text-muted)', label: 'Skipped', mark: '–' },
};

export const KIND_LABEL: Record<MigrationKind, string> = { schema: 'Schema', data: 'Data' };

export function migrationStatusStyle(status: string) {
  return MIGRATION_STATUS[status as MigrationRunStatus] ?? MIGRATION_STATUS.skipped;
}

export function migrationItemStyle(status: string) {
  return MIGRATION_ITEM_STATUS[status as MigrationItemStatus] ?? MIGRATION_ITEM_STATUS.pending;
}

/** "Data migration pending", "Schema migration failed". */
export function flagLabel(flag: Pick<MigrationFlag, 'kind' | 'status'>): string {
  return `${KIND_LABEL[flag.kind] ?? flag.kind} migration ${migrationStatusStyle(flag.status).label.toLowerCase()}`;
}

export function migrationPath(id: string): string {
  return `/migrations/${id}`;
}

/** "1m 12s", "450 ms" — durations as the migration library reports them, made readable. */
export function formatDurationMs(ms: number | null | undefined): string | null {
  if (ms == null) return null;
  // The library reports whole seconds, so 0 means "under a second", not an instant.
  if (ms < 1000) return ms === 0 ? '<1s' : `${ms} ms`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60 ? `${s % 60}s` : ''}`.trim();
  return `${Math.floor(m / 60)}h ${m % 60 ? `${m % 60}m` : ''}`.trim();
}

/**
 * The flags that belong on a deployment: its own schema migration (by deploy event), and the data
 * migration of its version in its environment — matched by version too, since a data migration can
 * be reported before the deploy it belongs to arrives.
 */
export function flagsForDeployment(
  flags: MigrationFlag[],
  deploy: { id: string; service: string; environment: string; version: string },
): MigrationFlag[] {
  return flags.filter(
    (f) =>
      f.deployEventId === deploy.id ||
      (f.kind === 'data' && f.service === deploy.service && f.environment === deploy.environment && f.version === deploy.version),
  );
}

/**
 * The flags for a matrix cell — what runs in (service, environment) now. A data migration still
 * open there belongs to the cell even after a redeploy of the same version (only one can be open per
 * cell, the rest are superseded); a schema migration only to the deploy that ran it.
 */
export function flagsForCell(
  flags: MigrationFlag[],
  cell: { id: string; service: string; environment: string },
): MigrationFlag[] {
  return flags.filter(
    (f) =>
      f.deployEventId === cell.id ||
      (f.kind === 'data' && f.service === cell.service && f.environment === cell.environment),
  );
}
