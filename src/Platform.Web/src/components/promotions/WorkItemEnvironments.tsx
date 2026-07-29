import { formatDistanceToNow } from 'date-fns';
import type { WorkItemEnvironment } from '@/lib/api';
import { EnvBadge } from '@/components/environments/EnvBadge';

/**
 * "Where can I see this?" for a work item — the environments the change is actually deployed to,
 * resolved server-side from the deploy events matching the carrying version.
 *
 * This replaced the source → target arrow the work-item rows used to show. That arrow described the
 * promotion, not the work item: the target environment is where the build is *asking* to go, which is
 * precisely the one place the change can't be tested yet. A reviewer needs the opposite — the
 * environments already running it.
 *
 * Each pill's tooltip carries the service, version, and how long ago it landed, so a stale
 * environment is identifiable without leaving the row.
 */
export function WorkItemEnvironments({
  environments,
  label = 'Testable in',
  size = 'xs',
}: {
  environments: WorkItemEnvironment[];
  label?: string;
  /** `xs` for dense queue rows, `sm` for the detail page header. */
  size?: 'xs' | 'sm';
}) {
  if (environments.length === 0) {
    return (
      <span className="inline-flex items-center gap-1">
        <span style={{ color: 'var(--text-muted)' }}>{label}:</span>
        <span
          style={{ color: 'var(--text-muted)' }}
          title="No succeeded deploy of this version has been recorded in any environment yet."
        >
          nowhere yet
        </span>
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1 flex-wrap">
      <span style={{ color: 'var(--text-muted)' }}>{label}:</span>
      {environments.map((e) => (
        <EnvBadge
          key={e.environment}
          env={e.environment}
          size={size}
          title={`${e.service} ${e.version} — deployed ${formatDistanceToNow(new Date(e.deployedAt), {
            addSuffix: true,
          })}`}
        />
      ))}
    </span>
  );
}
