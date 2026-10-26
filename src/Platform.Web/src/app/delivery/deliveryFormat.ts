import type { DeliveryGoodWhen, DeliveryMetric, DeliveryMetricUnit } from '@/lib/api';

/** Hours as people read them: minutes under an hour, hours under two days, days after. */
export function fmtHours(hours: number | null | undefined): string {
  if (hours == null) return '—';
  if (hours < 1) return `${Math.round(hours * 60)}m`;
  if (hours < 48) return `${Math.round(hours * 10) / 10}h`;
  return `${(hours / 24).toFixed(1)}d`;
}

export function fmtRatio(ratio: number | null | undefined): string {
  if (ratio == null) return '—';
  return `${Math.round(ratio * 100)}%`;
}

export function fmtCount(n: number | null | undefined): string {
  if (n == null) return '—';
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

export function fmtValue(unit: DeliveryMetricUnit, value: number | null | undefined): string {
  switch (unit) {
    case 'hours': return fmtHours(value);
    case 'ratio': return fmtRatio(value);
    default: return fmtCount(value);
  }
}

/** A signed change in the metric's own unit — percentage points for ratios. */
export function fmtChange(unit: DeliveryMetricUnit, change: number): string {
  const sign = change > 0 ? '+' : change < 0 ? '−' : '±';
  const abs = Math.abs(change);
  switch (unit) {
    case 'hours': return `${sign}${fmtHours(abs)}`;
    case 'ratio': return `${sign}${Math.round(abs * 100)} pp`;
    default: return `${sign}${fmtCount(abs)}`;
  }
}

/** Whether a movement reads as improvement — drives colour, never the arrow. */
export function isGood(goodWhen: DeliveryGoodWhen, change: number): boolean | null {
  if (goodWhen === 'none' || change === 0) return null;
  return goodWhen === 'up' ? change > 0 : change < 0;
}

export function fmtDate(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleDateString() : '—';
}

export function fmtDateTime(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '—';
}

export function fmtAgo(iso: string | null | undefined): string {
  if (!iso) return '—';
  const hours = (Date.now() - new Date(iso).getTime()) / 3600_000;
  if (hours < 1) return 'just now';
  return `${fmtHours(hours)} ago`;
}

/** The one-line qualifier under a metric's value: sample size, and coverage where it has one. */
export function metricSub(m: DeliveryMetric): string {
  if (!m.available) return 'not measurable yet';
  const parts: string[] = [];
  if (m.unit === 'ratio' || m.unit === 'hours') parts.push(`n=${m.n}`);
  if (m.coverage) parts.push(`coverage ${fmtRatio(m.coverage.ratio)}`);
  if (m.percentiles && m.unit === 'hours' && m.percentiles.p90 != null) parts.push(`p90 ${fmtHours(m.percentiles.p90)}`);
  return parts.join(' · ');
}

/** RFC 4180-enough: quote everything, double the quotes. */
export function csvLine(cells: (string | number | null | undefined)[]): string {
  return cells
    .map((c) => (c == null ? '' : `"${String(c).replace(/"/g, '""')}"`))
    .join(',');
}
