import { useId } from 'react';
import { TrendingDown, TrendingUp } from 'lucide-react';
import type { DeliveryMetric } from '@/lib/api';
import { InfoPopover } from '@/components/ui/InfoPopover';
import { fmtChange, fmtValue, isGood, metricSub } from './deliveryFormat';

/**
 * One metric of the report. Two sizes: `primary` for the DORA four, `compact` for everything
 * else — same data, the big cards just get room for a sparkline. Delta colour follows the
 * metric's own `goodWhen`, never the direction: a falling failure rate is green.
 */
export function MetricCard({
  metric,
  size = 'compact',
  periodLabel,
}: {
  metric: DeliveryMetric;
  size?: 'primary' | 'compact';
  periodLabel: string;
}) {
  const change = metric.delta?.change ?? null;
  const good = change != null ? isGood(metric.goodWhen, change) : null;
  const deltaColor = good == null ? 'var(--text-muted)' : good ? 'var(--success)' : 'var(--danger)';
  const info = {
    what: metric.available
      ? metric.title
      : <>{metric.title} — <b>not measurable yet.</b> {metric.reason}</>,
    how: metric.definition,
    why: metric.delta?.previous != null
      ? `The delta compares the equal-length window immediately before (${fmtValue(metric.unit, metric.delta.previous)}, n=${metric.delta.previousN}).`
      : 'No previous-window figure to compare against yet.',
  };

  return (
    <div
      className={`relative rounded-xl border ${size === 'primary' ? 'p-4' : 'p-3'}`}
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        opacity: metric.available ? 1 : 0.7,
      }}
    >
      <span className="absolute top-1.5 right-1.5">
        <InfoPopover label={metric.title} content={info} />
      </span>
      <p className={`${size === 'primary' ? 'text-[12px]' : 'text-[11px]'} pr-6 truncate`} style={{ color: 'var(--text-muted)' }}>
        {metric.title}
      </p>
      <div className="flex items-baseline gap-2 mt-1">
        <p className={`${size === 'primary' ? 'text-2xl' : 'text-lg'} font-semibold leading-none`}>
          {metric.available ? fmtValue(metric.unit, metric.value) : '—'}
        </p>
        {change != null && change !== 0 && (
          <span className="inline-flex items-center gap-0.5 text-[11px] font-medium" style={{ color: deltaColor }}>
            {change > 0 ? <TrendingUp size={11} /> : <TrendingDown size={11} />}
            {fmtChange(metric.unit, change)}
          </span>
        )}
      </div>
      <p className="text-[11px] mt-1 truncate" style={{ color: 'var(--text-muted)' }}>
        {metric.available ? metricSub(metric) || periodLabel : metric.reason}
      </p>
      {size === 'primary' && metric.available && metric.trend.length > 1 && (
        <Sparkline metric={metric} />
      )}
    </div>
  );
}

/**
 * The metric's own trend, one bar per bucket. Bars rather than a line because most of these are
 * counts over a bucket, and a bar at zero is visible where a line point at zero is not. Buckets
 * with no sample (n=0 on a percentile metric) draw as a gap, not as zero.
 */
function Sparkline({ metric }: { metric: DeliveryMetric }) {
  const id = useId();
  const H = 36;
  const points = metric.trend;
  const max = Math.max(1e-9, ...points.map((p) => p.value ?? 0));
  const slot = 100 / points.length;
  return (
    <svg
      width="100%"
      height={H}
      role="img"
      aria-labelledby={id}
      className="mt-2"
      style={{ display: 'block' }}
    >
      <title id={id}>{metric.title} per {metric.trend.length} buckets</title>
      {points.map((p, i) => {
        const x = i * slot;
        const inset = slot * 0.15;
        if (p.value == null) {
          return (
            <rect key={p.start} x={`${x + inset}%`} y={H - 2} width={`${slot - inset * 2}%`} height={2}
              fill="var(--border-color)" />
          );
        }
        const h = Math.max(2, Math.round((p.value / max) * (H - 4)));
        return (
          <g key={p.start}>
            <title>{p.start}: {fmtValue(metric.unit, p.value)} (n={p.n})</title>
            <rect x={`${x + inset}%`} y={H - h} width={`${slot - inset * 2}%`} height={h} rx={1.5}
              fill="var(--accent)" />
          </g>
        );
      })}
    </svg>
  );
}
