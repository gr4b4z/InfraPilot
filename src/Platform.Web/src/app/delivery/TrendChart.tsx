import { useId, useState } from 'react';
import type { DeliveryMetric, DeliveryTrendPoint } from '@/lib/api';
import { fmtRatio, fmtValue } from './deliveryFormat';
import { THIN_COVERAGE, type TrendSeries } from './trendStyle';


const W = 640;
const H = 200;
const PAD = { top: 10, right: 12, bottom: 26, left: 44 };

/**
 * A metric over time, drawn to be read rather than glanced at: dated x axis, labelled y gridlines,
 * the previous window as a dashed ghost aligned bucket-for-bucket, optional per-product overlays,
 * and thin-coverage buckets drawn hollow so a lead-time blip resting on three stories doesn't read
 * like a trend. Counts are bars (a bar at zero is visible, a line point at zero is not); durations
 * and ratios are lines. Hover readout lives in the header so nothing covers the marks.
 */
export function TrendChart({
  metric,
  showPrevious,
  overlays = [],
  periodLabel,
}: {
  metric: DeliveryMetric;
  showPrevious: boolean;
  overlays?: TrendSeries[];
  periodLabel: string;
}) {
  const id = useId();
  const [hover, setHover] = useState<number | null>(null);
  const points = metric.trend;
  const previous = metric.previousTrend ?? [];
  const n = points.length;

  if (!metric.available || n === 0) {
    return (
      <ChartFrame title={metric.title} note={metric.available ? 'no data in this window' : metric.reason ?? 'not measurable yet'}>
        <div className="h-[200px] flex items-center justify-center text-[12px]" style={{ color: 'var(--text-muted)' }}>
          {metric.available ? 'Nothing to draw yet.' : metric.reason}
        </div>
      </ChartFrame>
    );
  }

  const allValues = [
    ...points.map((p) => p.value),
    ...(showPrevious ? previous.map((p) => p.value) : []),
    ...overlays.flatMap((o) => o.points.map((p) => p.value)),
  ].filter((v): v is number => v != null);
  const rawMax = Math.max(1e-9, ...allValues);
  const max = niceMax(rawMax, metric.unit === 'ratio');
  const plotW = W - PAD.left - PAD.right;
  const plotH = H - PAD.top - PAD.bottom;
  const slot = plotW / n;
  const x = (i: number) => PAD.left + slot * i + slot / 2;
  const y = (v: number) => PAD.top + plotH - (v / max) * plotH;
  const isBar = metric.unit === 'count';
  const gridValues = [0, max / 2, max];

  const path = (pts: DeliveryTrendPoint[]) =>
    pts.map((p, i) => (p.value == null ? null : `${x(i)},${y(p.value)}`))
      .reduce<string[]>((acc, cur, i, arr) => {
        if (cur == null) return acc;
        const prev = i > 0 ? arr[i - 1] : null;
        acc.push(`${prev == null ? 'M' : 'L'}${cur}`);
        return acc;
      }, [])
      .join(' ');

  const hovered = hover != null ? points[hover] : null;
  const hoveredPrev = hover != null ? previous[hover] : undefined;
  const thin = (p: DeliveryTrendPoint) => p.coverage != null && p.coverage < THIN_COVERAGE;

  return (
    <ChartFrame
      title={metric.title}
      note={periodLabel}
      readout={
        hovered ? (
          <span className="inline-flex flex-wrap items-center gap-x-2">
            <span>{hovered.start}</span>
            <b style={{ color: 'var(--text-primary)' }}>{fmtValue(metric.unit, hovered.value)}</b>
            <span>n={hovered.n}</span>
            {hovered.coverage != null && (
              <span style={{ color: thin(hovered) ? 'var(--warning)' : undefined }}>coverage {fmtRatio(hovered.coverage)}</span>
            )}
            {showPrevious && hoveredPrev && (
              <span>· prev {fmtValue(metric.unit, hoveredPrev.value)} (n={hoveredPrev.n})</span>
            )}
            {overlays.map((o) => {
              const op = o.points[hover!];
              return op && op.value != null ? (
                <span key={o.name} className="inline-flex items-center gap-1">
                  <span className="inline-block w-2 h-2 rounded-full" style={{ backgroundColor: o.color }} />
                  {o.name} {fmtValue(metric.unit, op.value)}
                </span>
              ) : null;
            })}
          </span>
        ) : (
          <span>hover a bucket for values{showPrevious ? ' · dashed = previous period' : ''}</span>
        )
      }
    >
      <svg
        viewBox={`0 0 ${W} ${H}`}
        width="100%"
        height={H}
        preserveAspectRatio="none"
        role="img"
        aria-labelledby={id}
        onMouseLeave={() => setHover(null)}
        style={{ display: 'block', overflow: 'visible' }}
      >
        <title id={id}>{metric.title} per {points.length} buckets</title>
        {/* Gridlines and y labels. Text is rendered with a counter-scale so stretching the SVG doesn't distort it. */}
        {gridValues.map((v) => (
          <g key={v}>
            <line x1={PAD.left} x2={W - PAD.right} y1={y(v)} y2={y(v)} stroke="var(--border-color)" strokeDasharray={v === 0 ? undefined : '2 3'} vectorEffect="non-scaling-stroke" />
          </g>
        ))}
        {/* Previous window ghost */}
        {showPrevious && previous.length > 0 && (
          isBar ? (
            previous.slice(0, n).map((p, i) => p.value == null ? null : (
              <rect key={`g${p.start}`} x={x(i) - slot * 0.32} y={y(p.value)} width={slot * 0.64} height={Math.max(0, y(0) - y(p.value))}
                fill="none" stroke="var(--text-muted)" strokeDasharray="3 3" vectorEffect="non-scaling-stroke" opacity={0.7} />
            ))
          ) : (
            <path d={path(previous.slice(0, n))} fill="none" stroke="var(--text-muted)" strokeWidth={1.5} strokeDasharray="4 4" vectorEffect="non-scaling-stroke" opacity={0.8} />
          )
        )}
        {/* Product overlays */}
        {overlays.map((o) => (
          <g key={o.name}>
            <path d={path(o.points.slice(0, n))} fill="none" stroke={o.color} strokeWidth={1.5} vectorEffect="non-scaling-stroke" opacity={0.9} />
            {o.points.slice(0, n).map((p, i) => p.value == null ? null : (
              <circle key={p.start} cx={x(i)} cy={y(p.value)} r={2.5} fill={o.color} />
            ))}
          </g>
        ))}
        {/* Main series */}
        {isBar ? (
          points.map((p, i) => (
            <rect key={p.start} x={x(i) - slot * 0.32} y={y(p.value ?? 0)} width={slot * 0.64} height={Math.max(0, y(0) - y(p.value ?? 0))}
              fill="var(--accent)" rx={2} opacity={hover === i ? 1 : 0.85} />
          ))
        ) : (
          <>
            <path d={path(points)} fill="none" stroke="var(--accent)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
            {points.map((p, i) => p.value == null ? (
              <line key={p.start} x1={x(i) - slot * 0.2} x2={x(i) + slot * 0.2} y1={y(0)} y2={y(0)} stroke="var(--border-color)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
            ) : (
              <circle key={p.start} cx={x(i)} cy={y(p.value)} r={hover === i ? 5 : 3.5}
                fill={thin(p) ? 'var(--bg-primary)' : 'var(--accent)'} stroke="var(--accent)" strokeWidth={2} vectorEffect="non-scaling-stroke" />
            ))}
          </>
        )}
        {/* Hover slots */}
        {points.map((p, i) => (
          <rect key={`h${p.start}`} x={PAD.left + slot * i} y={PAD.top} width={slot} height={plotH} fill="transparent" onMouseEnter={() => setHover(i)} />
        ))}
      </svg>
      {/* Axis labels as HTML so they never stretch with the SVG. */}
      <div className="relative text-[10px] mt-1" style={{ color: 'var(--text-muted)', paddingLeft: `${(PAD.left / W) * 100}%`, paddingRight: `${(PAD.right / W) * 100}%` }}>
        <div className="flex justify-between">
          <span>{points[0].start}</span>
          {n > 2 && <span>{points[Math.floor((n - 1) / 2)].start}</span>}
          <span>{points[n - 1].start}</span>
        </div>
      </div>
      <div className="absolute text-[10px] text-right" style={{ color: 'var(--text-muted)', left: 0, width: `${(PAD.left / W) * 100}%`, top: 0, height: H, pointerEvents: 'none' }} aria-hidden>
        {gridValues.map((v) => (
          <span key={v} className="absolute right-2" style={{ top: `${(y(v) / H) * 100}%`, transform: 'translateY(-50%)' }}>
            {v === 0 ? '0' : fmtValue(metric.unit, v)}
          </span>
        ))}
      </div>
    </ChartFrame>
  );
}

function ChartFrame({ title, note, readout, children }: { title: string; note?: string; readout?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex items-baseline justify-between gap-3 mb-2">
        <h3 className="text-[13px] font-semibold">
          {title}
          {note && <span className="font-normal text-[11px] ml-2" style={{ color: 'var(--text-muted)' }}>{note}</span>}
        </h3>
        <div className="text-[11px] min-h-4 text-right" style={{ color: 'var(--text-muted)' }}>{readout}</div>
      </div>
      <div className="relative">{children}</div>
    </section>
  );
}

/** A round axis maximum: 1-2-5 steps; ratios cap at 100%. */
function niceMax(v: number, ratio: boolean): number {
  if (ratio) return v <= 0.1 ? 0.1 : v <= 0.25 ? 0.25 : v <= 0.5 ? 0.5 : 1;
  const mag = Math.pow(10, Math.floor(Math.log10(v)));
  const norm = v / mag;
  const step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
  return step * mag;
}

