import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { format, subMonths } from 'date-fns';
import { api } from '@/lib/api';
import { deploymentDetailPath } from '@/lib/deploymentPath';
import { useSettingsStore } from '@/stores/settingsStore';
import { useEnvColor } from '@/components/environments/useEnvColor';
import { EnvLabel } from '@/components/environments/EnvBadge';
import type { DeployEvent } from '@/lib/types';

/** How much history feeds the diagram. Bounded so a hyperactive service stays readable. */
const HISTORY_LIMIT = 200;

const STATUS_LABEL: Record<string, string> = {
  succeeded: 'Succeeded',
  failed: 'Failed',
  in_progress: 'In progress',
};

/**
 * Releases over the last month, one lane per environment — when did versions land where, at a
 * glance. A dot is a deploy event placed on a shared linear time axis and wearing its
 * environment's colour (redundant with the lane label on purpose: operator-configured hues may
 * collide, the label is what identifies the lane). Failure and rollback override the shape/colour
 * instead of adding a second axis: failed fills danger, a rollback is a hollow ring. The hover
 * readout sits in the header row, never over the marks — same contract as DeployTrendChart.
 *
 * The window is fixed to the trailing month rather than fitted to the data: a sparse right edge
 * or an empty left half is itself information (cadence slowed), which a data-fitted axis would
 * silently stretch away.
 */
export function ReleaseTimeline({ product, service, backHref, refreshTick }: {
  product: string;
  service: string;
  /** Back-link target the deploy event detail pages should return to (this page). */
  backHref: string;
  /** Realtime deployments tick — bump refetches, exactly like the page's main fetch. */
  refreshTick: number;
}) {
  const { getOrderedEnvironments, getDisplayName } = useSettingsStore();
  // "Now" is captured when the fetch lands, not during render (render must stay pure); the
  // realtime tick refetches, so the window's right edge tracks reality closely enough.
  const [history, setHistory] = useState<{ events: DeployEvent[]; asOf: number } | null>(null);
  const [hovered, setHovered] = useState<DeployEvent | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .getDeploymentHistory(product, service, { limit: HISTORY_LIMIT })
      .then((evts) => {
        if (!cancelled) setHistory({ events: evts, asOf: Date.now() });
      })
      .catch(() => {
        // The page's own fetch reports failure; a missing diagram shouldn't add a second error.
        if (!cancelled) setHistory(null);
      });
    return () => {
      cancelled = true;
    };
  }, [product, service, refreshTick]);

  // The trailing-month domain, padded so edge dots don't sit on the border. The right edge
  // stretches to cover a clock-skewed event stamped slightly in the future rather than clipping it.
  const domain = useMemo(() => {
    if (!history || history.events.length === 0) return null;
    const newest = Math.max(...history.events.map((e) => new Date(e.deployedAt).getTime()));
    const end = Math.max(history.asOf, newest);
    const cutoff = subMonths(end, 1).getTime();
    const pad = (end - cutoff) * 0.02;
    return { start: cutoff - pad, end: end + pad, cutoff };
  }, [history]);

  const visible = useMemo(
    () =>
      domain && history
        ? history.events.filter((e) => new Date(e.deployedAt).getTime() >= domain.cutoff)
        : [],
    [history, domain],
  );

  const lanes = useMemo(() => {
    const byEnv = new Map<string, DeployEvent[]>();
    for (const e of visible) {
      let lane = byEnv.get(e.environment);
      if (!lane) byEnv.set(e.environment, (lane = []));
      lane.push(e);
    }
    // Oldest first within a lane, so a later deploy paints over an earlier one when they collide.
    for (const lane of byEnv.values()) lane.reverse();
    return getOrderedEnvironments(Array.from(byEnv.keys())).map((env) => ({
      env,
      events: byEnv.get(env)!,
    }));
  }, [visible, getOrderedEnvironments]);

  // No history at all: nothing to draw and nothing to say. But history that is merely older than
  // the window gets a note instead — a silently vanished section would read as a bug.
  if (!domain) return null;

  const header = (
    <div className="flex items-baseline gap-3 mb-2">
      <h2 className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
        Release timeline
      </h2>
      {/* Readout lives up here so hovering a dot never covers its neighbours. */}
      <span className="text-[12px] truncate min-w-0" style={{ color: 'var(--text-muted)' }}>
        {hovered ? (
          <>
            <EnvLabel env={hovered.environment} className="font-semibold" />
            {' · '}
            <b className="font-mono" style={{ color: 'var(--text-primary)' }}>
              v{hovered.version}
            </b>
            {' · '}
            {STATUS_LABEL[hovered.status] ?? hovered.status}
            {hovered.isRollback ? ' · rollback' : ''}
            {' · '}
            {format(new Date(hovered.deployedAt), 'd MMM yyyy, HH:mm')}
          </>
        ) : (
          'last month · hover a release for details'
        )}
      </span>
    </div>
  );

  if (lanes.length === 0) {
    return (
      <section>
        {header}
        <p className="text-sm" style={{ color: 'var(--text-muted)' }}>
          No releases in the last month —{' '}
          <Link
            to={`${backHref}/history`}
            className="font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--accent)' }}
          >
            see the full history
          </Link>{' '}
          for older deploys.
        </p>
      </section>
    );
  }

  const positionOf = (e: DeployEvent) =>
    ((new Date(e.deployedAt).getTime() - domain.start) / (domain.end - domain.start)) * 100;

  const spansYears =
    new Date(domain.cutoff).getFullYear() !== new Date(domain.end).getFullYear();
  const axisFormat = spansYears ? 'd MMM yyyy' : 'd MMM';
  const hasFailures = visible.some((e) => e.status === 'failed');
  const hasRollbacks = visible.some((e) => e.isRollback);

  return (
    <section>
      {header}

      <div
        className="rounded-xl border px-3 py-2"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div className="flex">
          {/* Lane labels, stacked at the same row height as the lanes beside them. */}
          <div className="w-20 sm:w-28 shrink-0">
            {lanes.map(({ env }) => (
              <div key={env} className="h-9 flex items-center pr-2 min-w-0">
                <EnvLabel env={env} className="text-[12px] font-semibold truncate" />
              </div>
            ))}
          </div>

          <div className="relative flex-1 min-w-0">
            {/* Quarter gridlines, shared by every lane so vertical alignment is readable. */}
            {[25, 50, 75].map((pct) => (
              <span
                key={pct}
                aria-hidden
                className="absolute inset-y-1 w-px"
                style={{ left: `${pct}%`, backgroundColor: 'var(--border-color)' }}
              />
            ))}

            {lanes.map(({ env, events: laneEvents }) => (
              <div key={env} className="relative h-9">
                <span
                  aria-hidden
                  className="absolute left-0 right-0 top-1/2 h-px"
                  style={{ backgroundColor: 'var(--border-color)' }}
                />
                {laneEvents.map((e) => (
                  <TimelineDot
                    key={e.id}
                    event={e}
                    left={positionOf(e)}
                    href={deploymentDetailPath(e.id, { path: backHref, label: service })}
                    displayName={getDisplayName(e.environment)}
                    onHover={setHovered}
                  />
                ))}
              </div>
            ))}
          </div>
        </div>

        {/* The relative wrapper starts after the label gutter, so the absolutely-centred middle
           date sits exactly under the 50% gridline rather than in the leftover flex space. */}
        <div className="relative flex items-center gap-3 mt-1 ml-20 sm:ml-28">
          <span className="text-[10px]" style={{ color: 'var(--text-muted)' }}>
            {format(new Date(domain.cutoff), axisFormat)}
          </span>
          <span className="flex-1" />
          <span
            className="absolute left-1/2 -translate-x-1/2 text-[10px] hidden sm:block"
            style={{ color: 'var(--text-muted)' }}
          >
            {format(new Date((domain.cutoff + domain.end) / 2), axisFormat)}
          </span>
          {(hasFailures || hasRollbacks) && (
            <span
              className="hidden sm:inline-flex items-center gap-3 text-[10px]"
              style={{ color: 'var(--text-muted)' }}
            >
              {hasFailures && (
                <span className="inline-flex items-center gap-1">
                  <span
                    className="inline-block w-2 h-2 rounded-full"
                    style={{ backgroundColor: 'var(--danger)' }}
                  />
                  failed
                </span>
              )}
              {hasRollbacks && (
                <span className="inline-flex items-center gap-1">
                  <span
                    className="inline-block w-2 h-2 rounded-full border-2"
                    style={{ borderColor: 'var(--text-muted)' }}
                  />
                  rollback
                </span>
              )}
            </span>
          )}
          <span className="text-[10px]" style={{ color: 'var(--text-muted)' }}>
            today
          </span>
        </div>
      </div>
    </section>
  );
}

/**
 * One deploy event on its lane: a link to the event's detail page, so the diagram is a map of the
 * history rather than a dead drawing. The ring of card-background around every dot is what keeps
 * a burst of near-simultaneous deploys readable as separate marks instead of one blob.
 */
function TimelineDot({ event: e, left, href, displayName, onHover }: {
  event: DeployEvent;
  left: number;
  href: string;
  displayName: string;
  onHover: (e: DeployEvent | null) => void;
}) {
  const { solid } = useEnvColor(e.environment);
  const color =
    e.status === 'failed' ? 'var(--danger)' : e.status === 'in_progress' ? 'var(--warning)' : solid;

  const label =
    `${displayName}: v${e.version}, ${STATUS_LABEL[e.status] ?? e.status}` +
    `${e.isRollback ? ', rollback' : ''}, ${format(new Date(e.deployedAt), 'd MMM yyyy, HH:mm')}`;

  return (
    <Link
      to={href}
      aria-label={`${label}. Open deployment.`}
      title={label}
      onMouseEnter={() => onHover(e)}
      onMouseLeave={() => onHover(null)}
      onFocus={() => onHover(e)}
      onBlur={() => onHover(null)}
      className="absolute top-1/2 rounded-full transition-transform hover:scale-125 focus-visible:scale-125"
      style={{
        left: `${left}%`,
        width: 11,
        height: 11,
        transform: 'translate(-50%, -50%)',
        // A rollback is a hollow ring in the same colour; everything else is a filled dot.
        backgroundColor: e.isRollback ? 'var(--bg-secondary)' : color,
        border: e.isRollback ? `2.5px solid ${color}` : 'none',
        boxShadow: '0 0 0 1.5px var(--bg-secondary)',
      }}
    />
  );
}
