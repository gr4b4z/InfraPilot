import type { LucideIcon } from 'lucide-react';
import { TrendingDown, TrendingUp } from 'lucide-react';

export interface StatTileDelta {
  /** Formatted delta, e.g. "+3" or "-4h". */
  text: string;
  /** Whether this movement is an improvement — drives the color, not the arrow. */
  good: boolean;
  /** Arrow direction: did the number go up or down. */
  up: boolean;
}

export interface StatTile {
  label: string;
  value: string;
  /** Small line under the label — sample size, qualifier ("n=12", "vs prev 14d"). */
  sub?: string;
  icon: LucideIcon;
  color: string;
  bg: string;
  delta?: StatTileDelta;
  /** Dims the tile and hints the value is not available yet. */
  muted?: boolean;
}

/**
 * The KPI tile row used by the analytics executive strip — the same visual pattern the
 * approvals/catalog pages inline, extended with a delta-vs-previous-period arrow. Delta color
 * follows `good`, not direction: a falling failure rate is green, a falling deploy count is red.
 */
export function StatTiles({ tiles }: { tiles: StatTile[] }) {
  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-3">
      {tiles.map((t) => (
        <div
          key={t.label}
          className="flex items-center gap-3 p-3.5 rounded-xl border"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            opacity: t.muted ? 0.6 : 1,
          }}
        >
          <div
            className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0"
            style={{ backgroundColor: t.bg, color: t.color }}
          >
            <t.icon size={16} />
          </div>
          <div className="min-w-0">
            <div className="flex items-baseline gap-1.5">
              <p className="text-lg font-semibold leading-none truncate">{t.value}</p>
              {t.delta && (
                <span
                  className="inline-flex items-center gap-0.5 text-[11px] font-medium"
                  style={{ color: t.delta.good ? 'var(--success)' : 'var(--danger)' }}
                >
                  {t.delta.up ? <TrendingUp size={11} /> : <TrendingDown size={11} />}
                  {t.delta.text}
                </span>
              )}
            </div>
            <p className="text-[11px] mt-0.5 truncate" style={{ color: 'var(--text-muted)' }}>
              {t.label}
              {t.sub && <span> · {t.sub}</span>}
            </p>
          </div>
        </div>
      ))}
    </div>
  );
}
