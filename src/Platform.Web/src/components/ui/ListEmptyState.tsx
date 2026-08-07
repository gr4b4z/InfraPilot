import type { LucideIcon } from 'lucide-react';
import { X } from 'lucide-react';

/**
 * The empty panel a filtered list page shows instead of rows.
 *
 * "No items for the current filters" is the message that sends people to Slack: it doesn't say which
 * filters, whether the tab or the dropdowns are responsible, or how to get back to a list with
 * something in it. So this takes the filters themselves — one chip per narrowing, each clearable —
 * and a tone that says whether an empty list is good news or just a narrow view.
 *
 * The tone is carried by colour on the icon tile and the panel's border, not by the copy: an empty
 * "Needs attention" tab means everything is in order (green), an empty filtered list means the view
 * is narrow (accent), and an empty history means nothing has happened yet (neutral). Colour is
 * redundant with the title in every case — it's a scanning aid, never the only signal.
 */
export type EmptyStateTone = 'neutral' | 'good' | 'filtered';

/** One active narrowing, named and valued as the user set it. */
export interface ActiveFilterChip {
  /** What is being narrowed — "Product", "Target env". Matches the control's own label. */
  label: string;
  /** The pick, in the form the control displays it (environments use their display name). */
  value: string;
  /** Clears just this one. Omit for a narrowing the panel can't undo (e.g. the tab itself). */
  onClear?: () => void;
}

const TONES: Record<EmptyStateTone, { color: string; bg: string; border: string }> = {
  neutral: { color: 'var(--text-muted)', bg: 'var(--bg-secondary)', border: 'var(--border-color)' },
  good: { color: 'var(--success)', bg: 'var(--success-bg)', border: 'var(--success)' },
  filtered: { color: 'var(--accent)', bg: 'var(--accent-bg)', border: 'var(--accent)' },
};

export function ListEmptyState({
  icon: Icon,
  tone = 'neutral',
  title,
  body,
  filters = [],
  onClearFilters,
}: {
  icon: LucideIcon;
  tone?: EmptyStateTone;
  title: string;
  /** One or two sentences: what this list would contain, and what to do about it being empty. */
  body: string;
  /** The narrowings in effect. Rendered as clearable chips; empty means nothing is filtered. */
  filters?: ActiveFilterChip[];
  /** Clears every filter above at once. Only offered when more than one is set. */
  onClearFilters?: () => void;
}) {
  const t = TONES[tone];
  return (
    <div
      className="flex flex-col items-center justify-center px-6 py-16 rounded-xl border text-center"
      style={{ borderColor: t.border, backgroundColor: 'var(--bg-primary)' }}
    >
      <div
        className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
        style={{ backgroundColor: t.bg, color: t.color }}
      >
        <Icon size={24} />
      </div>
      <p className="text-[14px] font-medium" style={{ color: 'var(--text-primary)' }}>
        {title}
      </p>
      <p className="text-[13px] mt-1 max-w-[52ch]" style={{ color: 'var(--text-muted)' }}>
        {body}
      </p>

      {filters.length > 0 && (
        <div className="mt-4 flex flex-col items-center gap-2">
          <span
            className="text-[11px] font-semibold uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            Filters in effect
          </span>
          <div className="flex flex-wrap items-center justify-center gap-1.5">
            {filters.map((f) => (
              <span
                key={`${f.label}:${f.value}`}
                className="inline-flex items-center gap-1 rounded-full border py-0.5 pl-2 pr-1 text-[12px]"
                style={{
                  borderColor: 'var(--accent)',
                  backgroundColor: 'var(--accent-bg)',
                  color: 'var(--accent)',
                }}
              >
                <span style={{ opacity: 0.75 }}>{f.label}:</span>
                <span className="font-medium">{f.value}</span>
                {f.onClear ? (
                  <button
                    type="button"
                    onClick={f.onClear}
                    className="rounded-full p-0.5 transition-opacity hover:opacity-60"
                    aria-label={`Clear the ${f.label} filter`}
                    title={`Clear the ${f.label} filter`}
                  >
                    <X size={11} />
                  </button>
                ) : (
                  /* Keeps the chip's right padding even without a button, so a mixed row lines up. */
                  <span className="pr-1" />
                )}
              </span>
            ))}
          </div>
          {onClearFilters && filters.length > 1 && (
            <button
              type="button"
              onClick={onClearFilters}
              className="mt-1 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-colors"
              style={{
                borderColor: 'var(--accent)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--accent)',
              }}
            >
              Clear all filters
            </button>
          )}
        </div>
      )}
    </div>
  );
}
