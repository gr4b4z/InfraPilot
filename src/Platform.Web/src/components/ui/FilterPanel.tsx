import { useId, useState, type ReactNode } from 'react';
import { ChevronDown, SlidersHorizontal } from 'lucide-react';

/**
 * Wrapper for a list page's filter row.
 *
 * On a wide viewport the filters sit inline above the list, as they always have. On a narrow one the
 * same controls stack — four full-width selects and text inputs push the list itself below the fold,
 * so the row folds into a single "Filters" button and only expands when asked for.
 *
 * The collapse is narrow-screen only: at `lg` and up the panel is always open and the toggle is
 * hidden, so the desktop layout is unchanged and there is no state to get stuck in.
 *
 * `activeCount` is what makes the collapsed state safe. A hidden filter that is silently narrowing
 * the list is how you end up debugging an empty table for ten minutes, so the count rides on the
 * button and the panel starts open when anything is already set. It only seeds the initial state —
 * a filter being active must not pin the panel open, or the toggle stops responding exactly when
 * the row is tallest.
 *
 * ```tsx
 * <FilterPanel activeCount={[productFilter, envFilter].filter(Boolean).length}>
 *   <select …/>
 *   <select …/>
 * </FilterPanel>
 * ```
 *
 * Children are laid out in a wrapping flex row. Direct children stretch to full width below `sm`
 * so native selects don't end up at their intrinsic (and inconsistent) widths.
 */
/**
 * Classes for the label half of a "label + native select" filter pair.
 *
 * Below `sm` the pair claims a full-width row — label pinned left, control stretched to the right
 * edge — so a stacked column of filters lines up instead of ragging at each select's intrinsic
 * width. From `sm` up the pairs sit inline on one wrapping row, as they do on desktop today.
 */
export const filterLabelClass =
  'flex w-full items-center justify-between gap-2 text-[12px] sm:inline-flex sm:w-auto sm:justify-start sm:gap-1.5';

/** Companion to {@link filterLabelClass} for the select it wraps. */
export const filterSelectClass =
  'min-w-0 flex-1 rounded-lg border px-2 py-1.5 text-[12px] font-medium sm:flex-none';

export function FilterPanel({
  children,
  activeCount = 0,
  label = 'Filters',
  badge,
}: {
  children: ReactNode;
  /**
   * Number of filters currently narrowing the list. Drives the toggle's accent treatment and
   * whether the panel starts open, and is what the pill shows unless {@link badge} overrides it.
   */
  activeCount?: number;
  label?: string;
  /**
   * Replaces the `activeCount` number in the toggle's pill. For pages where the count that matters
   * to the reader isn't "how many filters are set" — the deployments overview reports how many
   * products survived the filter, which is the set you actually want read back. Shown whenever
   * provided, including at `activeCount === 0`; the accent and auto-open still follow
   * `activeCount`, so the "something is hidden" signal is not lost.
   */
  badge?: ReactNode;
}) {
  const contentId = useId();
  const [expanded, setExpanded] = useState(activeCount > 0);

  return (
    <div>
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        aria-controls={contentId}
        className="lg:hidden flex items-center gap-2 w-full rounded-lg border px-3 py-2 text-[13px] font-medium transition-colors"
        style={{
          borderColor: activeCount > 0 ? 'var(--accent)' : 'var(--border-color)',
          backgroundColor: activeCount > 0 ? 'var(--accent-bg)' : 'var(--bg-primary)',
          color: activeCount > 0 ? 'var(--accent)' : 'var(--text-secondary)',
        }}
      >
        <SlidersHorizontal size={14} />
        <span>{label}</span>
        {(badge ?? (activeCount > 0 ? activeCount : null)) !== null && (
          <span
            className="px-1.5 rounded-full text-[11px] font-semibold"
            style={{
              backgroundColor: activeCount > 0 ? 'var(--accent)' : 'var(--bg-secondary)',
              color: activeCount > 0 ? '#fff' : 'var(--text-muted)',
            }}
          >
            {badge ?? activeCount}
          </span>
        )}
        <ChevronDown
          size={14}
          className={`ml-auto transition-transform duration-150 ${expanded ? 'rotate-180' : ''}`}
        />
      </button>

      <div
        id={contentId}
        className={`${
          expanded ? 'flex' : 'hidden'
        } lg:flex flex-wrap items-center gap-2 sm:gap-3 mt-2 lg:mt-0 [&>*]:w-full sm:[&>*]:w-auto [&>*]:min-w-0`}
      >
        {children}
      </div>
    </div>
  );
}
