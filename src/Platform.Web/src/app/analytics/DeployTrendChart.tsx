import { useMemo, useState } from 'react';
import type { FrequencyResponse } from '@/lib/api';
import { EnvDot } from '@/components/environments/EnvBadge';

interface TrendBucket {
  start: string;
  total: number;
  failed: number;
  byEnv: [string, number][];
}

/**
 * Deployments over time — one bar per bucket, single accent series. Deliberately NOT stacked by
 * environment: env colors are operator-configured, so adjacent stack segments can land on
 * near-identical hues; the per-environment breakdown lives in the tooltip instead, where each
 * env is named next to its dot. Failed attempts overlay the bar top in the danger color so a bad
 * day is visible at a glance without a second axis.
 */
export function DeployTrendChart({ frequency }: { frequency: FrequencyResponse | null }) {
  const [hover, setHover] = useState<number | null>(null);

  const buckets = useMemo<TrendBucket[]>(() => {
    if (!frequency) return [];
    const map = new Map<string, TrendBucket>();
    for (const series of frequency.series) {
      const env = series.key.environment;
      for (const b of series.buckets) {
        let agg = map.get(b.start);
        if (!agg) map.set(b.start, (agg = { start: b.start, total: 0, failed: 0, byEnv: [] }));
        agg.total += b.count;
        agg.failed += b.failed;
        if (env && b.count > 0) agg.byEnv.push([env, b.count]);
      }
    }
    return [...map.values()].sort((a, b) => a.start.localeCompare(b.start));
  }, [frequency]);

  if (buckets.length === 0) return null;
  const max = Math.max(1, ...buckets.map((b) => b.total + b.failed));
  const H = 96;
  const hovered = hover != null ? buckets[hover] : null;

  return (
    <section
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-baseline justify-between mb-2">
        <h2 className="text-[13px] font-semibold">Deployments per day · all environments</h2>
        {/* Tooltip readout lives in the header row, so hovering never covers the bars. */}
        <div className="text-[12px] h-4" style={{ color: 'var(--text-muted)' }}>
          {hovered ? (
            <span className="inline-flex items-center gap-2">
              <span>{hovered.start}</span>
              <b style={{ color: 'var(--text-primary)' }}>{hovered.total}</b>
              {hovered.failed > 0 && (
                <span style={{ color: 'var(--danger)' }}>{hovered.failed} failed</span>
              )}
              {hovered.byEnv.map(([env, n]) => (
                <span key={env} className="inline-flex items-center gap-1">
                  <EnvDot env={env} size={7} />
                  {n}
                </span>
              ))}
            </span>
          ) : (
            <span>hover for the per-environment split</span>
          )}
        </div>
      </div>
      <svg
        width="100%"
        height={H}
        role="img"
        aria-label="Deployments per day"
        onMouseLeave={() => setHover(null)}
        style={{ display: 'block' }}
      >
        {buckets.map((b, i) => {
          const slot = 100 / buckets.length;
          const x = i * slot;
          const okH = Math.round((b.total / max) * (H - 8));
          const failH = Math.round((b.failed / max) * (H - 8));
          const barW = Math.max(30, 100 - buckets.length); // % of the slot, thin at low counts
          const inset = (slot * (100 - barW)) / 200;
          return (
            <g key={b.start}>
              {/* Hover hit target: the whole slot, much bigger than the mark. */}
              <rect
                x={`${x}%`}
                y={0}
                width={`${slot}%`}
                height={H}
                fill={hover === i ? 'var(--bg-secondary)' : 'transparent'}
                onMouseEnter={() => setHover(i)}
              />
              {failH > 0 && (
                <rect
                  x={`${x + inset}%`}
                  y={H - okH - failH}
                  width={`${slot - inset * 2}%`}
                  height={failH}
                  rx={2}
                  fill="var(--danger)"
                  pointerEvents="none"
                />
              )}
              {okH > 0 && (
                <rect
                  x={`${x + inset}%`}
                  y={H - okH}
                  width={`${slot - inset * 2}%`}
                  height={okH}
                  rx={2}
                  fill="var(--accent)"
                  pointerEvents="none"
                />
              )}
              {b.total + b.failed === 0 && (
                <rect
                  x={`${x + inset}%`}
                  y={H - 2}
                  width={`${slot - inset * 2}%`}
                  height={2}
                  fill="var(--border-color)"
                  pointerEvents="none"
                />
              )}
            </g>
          );
        })}
      </svg>
      <div className="flex justify-between text-[10px] mt-1" style={{ color: 'var(--text-muted)' }}>
        <span>{buckets[0].start}</span>
        <span>{buckets[buckets.length - 1].start}</span>
      </div>
    </section>
  );
}
