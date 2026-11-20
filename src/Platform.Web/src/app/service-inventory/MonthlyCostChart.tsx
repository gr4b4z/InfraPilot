import { useId, useState } from 'react';
import { SERIES_DIRECT, SERIES_SHARED, formatAmount, monthLabel } from './inventoryFormat';
import { Swatch } from './InventoryUi';

export interface MonthBar {
  period: string;
  complete: boolean;
  direct: number;
  shared: number;
}

/**
 * Cost per month in one currency, direct and shared stacked. Built like the analytics
 * DeployTrendChart: hand-drawn SVG, the readout in the header row so hovering never covers a bar.
 * The month to date is hatched and labelled "to date", so "incomplete" never depends on colour.
 */
export function MonthlyCostChart({ title, currency, bars }: { title: string; currency: string; bars: MonthBar[] }) {
  const [hover, setHover] = useState<number | null>(null);
  const [asTable, setAsTable] = useState(false);
  const id = useId().replace(/:/g, '');
  if (bars.length === 0) return null;

  const H = 140;
  const max = Math.max(1, ...bars.map((b) => b.direct + b.shared));
  const hovered = hover != null ? bars[hover] : null;
  const scale = (v: number) => (v / max) * (H - 18);

  return (
    <section className="rounded-xl border p-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 mb-2">
        <h2 className="text-[13px] font-semibold">{title}</h2>
        <span className="inline-flex flex-wrap items-center gap-3 text-[11px]" style={{ color: 'var(--text-secondary)' }}>
          <span className="inline-flex items-center gap-1"><Swatch color={SERIES_DIRECT} /> Direct</span>
          <span className="inline-flex items-center gap-1"><Swatch color={SERIES_SHARED} /> Shared</span>
          <span className="inline-flex items-center gap-1"><Swatch color="var(--text-muted)" hatched /> Month to date</span>
        </span>
        <span className="flex-1" />
        <button
          type="button"
          onClick={() => setAsTable((v) => !v)}
          className="text-[11px] hover:underline"
          style={{ color: 'var(--text-muted)' }}
        >
          {asTable ? 'Show chart' : 'Show as table'}
        </button>
      </div>

      {asTable ? (
        <table className="w-full text-[12px] tabular-nums">
          <thead>
            <tr style={{ color: 'var(--text-muted)' }}>
              <th className="text-left font-medium py-1">Month</th>
              <th className="text-right font-medium py-1">Direct</th>
              <th className="text-right font-medium py-1">Shared</th>
            </tr>
          </thead>
          <tbody>
            {bars.map((b) => (
              <tr key={b.period} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                <td className="py-1">{monthLabel(b.period)}{b.complete ? '' : ' (to date)'}</td>
                <td className="py-1 text-right">{formatAmount(b.direct, currency)}</td>
                <td className="py-1 text-right">{formatAmount(b.shared, currency)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <>
          <div className="text-[12px] h-4 mb-1" style={{ color: 'var(--text-muted)' }}>
            {hovered ? (
              <span className="inline-flex flex-wrap items-center gap-x-3">
                <span>{monthLabel(hovered.period)}{hovered.complete ? '' : ' (to date)'}</span>
                <span>Direct <b style={{ color: 'var(--text-primary)' }}>{formatAmount(hovered.direct, currency)}</b></span>
                <span>Shared <b style={{ color: 'var(--text-primary)' }}>{formatAmount(hovered.shared, currency)}</b></span>
              </span>
            ) : (
              <span>hover a month for its amounts</span>
            )}
          </div>
          <svg width="100%" height={H} role="img" aria-label={title} onMouseLeave={() => setHover(null)} style={{ display: 'block' }}>
            <defs>
              {([['d', SERIES_DIRECT], ['s', SERIES_SHARED]] as const).map(([k, color]) => (
                <pattern key={k} id={`${id}-${k}`} width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
                  <rect width="5" height="5" fill={color} opacity="0.25" />
                  <rect width="2" height="5" fill={color} />
                </pattern>
              ))}
            </defs>
            <line x1="0" x2="100%" y1={H - 0.5} y2={H - 0.5} stroke="var(--border-color)" />
            {bars.map((b, i) => {
              const slot = 100 / bars.length;
              const x = i * slot;
              const inset = slot * 0.22;
              const dH = scale(b.direct);
              const sH = scale(b.shared);
              const gap = dH > 0 && sH > 0 ? 2 : 0;
              return (
                <g key={b.period}>
                  <rect
                    x={`${x}%`} y={0} width={`${slot}%`} height={H}
                    fill={hover === i ? 'var(--bg-secondary)' : 'transparent'}
                    onMouseEnter={() => setHover(i)}
                  />
                  {dH > 0 && (
                    <rect
                      x={`${x + inset}%`} y={H - dH} width={`${slot - inset * 2}%`} height={dH} rx={3}
                      fill={b.complete ? SERIES_DIRECT : `url(#${id}-d)`}
                      stroke={b.complete ? undefined : SERIES_DIRECT}
                      strokeDasharray={b.complete ? undefined : '3 2'}
                      pointerEvents="none"
                    />
                  )}
                  {sH > 0 && (
                    <rect
                      x={`${x + inset}%`} y={H - dH - gap - sH} width={`${slot - inset * 2}%`} height={sH} rx={3}
                      fill={b.complete ? SERIES_SHARED : `url(#${id}-s)`}
                      stroke={b.complete ? undefined : SERIES_SHARED}
                      strokeDasharray={b.complete ? undefined : '3 2'}
                      pointerEvents="none"
                    />
                  )}
                  {dH + sH === 0 && (
                    <rect x={`${x + inset}%`} y={H - 2} width={`${slot - inset * 2}%`} height={2} fill="var(--border-strong)" pointerEvents="none" />
                  )}
                  <text
                    x={`${x + slot / 2}%`} y={Math.max(11, H - dH - gap - sH - 5)} textAnchor="middle"
                    fontSize="10" fill="var(--text-muted)" pointerEvents="none"
                  >
                    {formatAmount(b.direct + b.shared, currency, true)}
                  </text>
                </g>
              );
            })}
          </svg>
          <div className="flex text-[10px] mt-1" style={{ color: 'var(--text-muted)' }}>
            {bars.map((b) => (
              <span key={b.period} className="flex-1 text-center">
                {monthLabel(b.period, true)}
                {!b.complete && ' · to date'}
              </span>
            ))}
          </div>
        </>
      )}
    </section>
  );
}
