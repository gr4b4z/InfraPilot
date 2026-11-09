import type { MigrationRunSummary } from '@/lib/types';

/** "3 applied · 1 failed", or what the run said instead when it named none. */
export function MigrationCounts({ run }: { run: MigrationRunSummary }) {
  if (run.totalCount === 0) {
    if (run.status === 'pending' || run.status === 'superseded') return <span style={{ color: 'var(--text-muted)' }}>—</span>;
    return <span style={{ color: 'var(--text-muted)' }}>{run.status === 'succeeded' ? 'nothing to run' : 'none named'}</span>;
  }
  return (
    <span>
      {run.appliedCount} applied
      {run.failedCount > 0 && <span style={{ color: 'var(--danger)' }}> · {run.failedCount} failed</span>}
      {run.totalCount - run.appliedCount - run.failedCount > 0 && (
        <span style={{ color: 'var(--text-muted)' }}> · {run.totalCount - run.appliedCount - run.failedCount} not applied</span>
      )}
    </span>
  );
}
