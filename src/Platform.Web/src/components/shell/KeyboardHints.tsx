import { useEffect, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { rowActionSelector, type RowAction } from '@/lib/keys';
import { KEYBOARD_ROW_SELECTOR } from '@/hooks/keyboardList';

/**
 * The shortcuts that actually work where you currently are.
 *
 * A help overlay behind `?` only helps people who already suspect there is something to learn. Keeping
 * a few live hints in the sidebar footer is what makes the keyboard model discoverable without anyone
 * going looking for it.
 *
 * The action hints are read from the page rather than from a table of routes. A hand-kept map drifted
 * the moment `o` moved off the promotions list — the shortcut was gone but the hint still advertised
 * it, which is worse than no hint at all. Now a hint appears exactly when the control it names is on
 * screen, so the cheatsheet cannot claim something the page can't do.
 *
 * Deliberately short. This sits under the navigation, and a full keymap there would be wallpaper that
 * nobody reads; `?` is still the complete list.
 */
interface Hint {
  keys: string[];
  label: string;
}

/** Always available, so they anchor the list wherever you are. */
const GLOBAL_HINTS: Hint[] = [
  { keys: [':'], label: 'Go to' },
  { keys: ['?'], label: 'All shortcuts' },
];

/**
 * Action shortcuts, in the order they should be offered. Each is shown only when the page renders a
 * control tagged for it — see `data-row-action` in `lib/keys`.
 */
const ACTION_HINTS: Array<{ action: RowAction; hint: Hint }> = [
  { action: 'open-external', hint: { keys: ['o'], label: 'Open in tracker' } },
  { action: 'assign', hint: { keys: ['a'], label: 'Assign' } },
  { action: 'approve', hint: { keys: ['A'], label: 'Approve' } },
  { action: 'reject', hint: { keys: ['R'], label: 'Reject' } },
  { action: 'issue', hint: { keys: ['I'], label: 'Issue' } },
  { action: 'block', hint: { keys: ['B'], label: 'Block' } },
];

/** Navigation hints, which depend on the shape of the page rather than on any one control. */
function navHints(pathname: string, hasRows: boolean, hasGrid: boolean): Hint[] {
  const hints: Hint[] = [];
  if (hasRows) {
    hints.push({ keys: ['↑', '↓'], label: hasGrid ? 'Service' : 'Move' });
    if (hasGrid) hints.push({ keys: ['←', '→'], label: 'Environment' });
    hints.push({ keys: ['Enter'], label: 'Open' });
  }
  // `/` is always bound; naming what it searches is what makes it worth a line.
  hints.push({ keys: ['/'], label: searchLabel(pathname) });
  // Only worth saying on a page you can back out of.
  if (pathname.split('/').filter(Boolean).length > 1) {
    hints.push({ keys: ['Esc'], label: 'Back' });
  }
  return hints;
}

function searchLabel(pathname: string): string {
  if (pathname.startsWith('/promotions')) return 'Find promotion';
  if (pathname.startsWith('/deployments')) return 'Find service';
  if (pathname.startsWith('/me/work-items')) return 'Filter queue';
  return 'Search';
}

export function KeyboardHints() {
  const { pathname } = useLocation();
  const [hints, setHints] = useState<Hint[]>([]);

  // Recomputed on navigation and whenever the page's content changes, because most of what this
  // reports arrives with a fetch rather than with the route. A MutationObserver rather than a poll so
  // it settles as soon as the page does; debounced because a list rendering fires a great many
  // mutations for one meaningful change.
  useEffect(() => {
    let frame: number | undefined;

    const recompute = () => {
      const hasRows = document.querySelector(KEYBOARD_ROW_SELECTOR) !== null;
      // A grid announces itself by having cells in the same row — only the deployment matrix does.
      const hasGrid = document.querySelector(`tr > ${KEYBOARD_ROW_SELECTOR} ~ ${KEYBOARD_ROW_SELECTOR}`) !== null;
      const actions = ACTION_HINTS
        .filter(({ action }) => document.querySelector(rowActionSelector(action)) !== null)
        .map(({ hint }) => hint);
      setHints([...actions, ...navHints(pathname, hasRows, hasGrid), ...GLOBAL_HINTS]);
    };

    const schedule = () => {
      if (frame !== undefined) window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(recompute);
    };

    schedule();
    const observer = new MutationObserver(schedule);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => {
      observer.disconnect();
      if (frame !== undefined) window.cancelAnimationFrame(frame);
    };
  }, [pathname]);

  return (
    <div className="px-1 pb-1 space-y-1" aria-label="Keyboard shortcuts for this page">
      {hints.map((hint) => (
        <div key={`${hint.keys.join()}-${hint.label}`} className="flex items-center justify-between gap-2">
          <span className="text-[10px] truncate" style={{ color: 'var(--text-muted)' }}>
            {hint.label}
          </span>
          <span className="flex items-center gap-0.5 shrink-0">
            {hint.keys.map((key) => (
              <kbd
                key={key}
                className="px-1 rounded text-[9px] font-mono font-semibold leading-[14px]"
                style={{
                  backgroundColor: 'var(--bg-primary)',
                  color: 'var(--text-secondary)',
                  border: '1px solid var(--border-color)',
                }}
              >
                {key}
              </kbd>
            ))}
          </span>
        </div>
      ))}
    </div>
  );
}
