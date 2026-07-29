import { useLocation } from 'react-router-dom';

/**
 * The handful of shortcuts that matter where you currently are.
 *
 * A help overlay behind `?` only helps people who already suspect there is something to learn. Keeping
 * three or four live hints in the sidebar footer is what makes the keyboard model discoverable without
 * anyone going looking for it — and because they change with the route, they double as a reminder of
 * what this page can do.
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

function hintsForPath(pathname: string): Hint[] {
  // Order matters: the more specific route has to be tested before its prefix.
  if (pathname.startsWith('/work-items/')) {
    return [
      { keys: ['A'], label: 'Approve' },
      { keys: ['I'], label: 'Issue' },
      { keys: ['B'], label: 'Block' },
      { keys: ['a'], label: 'Assign' },
      { keys: ['Esc'], label: 'Back' },
    ];
  }
  if (pathname.startsWith('/promotions/')) {
    return [
      { keys: ['A'], label: 'Approve' },
      { keys: ['R'], label: 'Reject' },
      { keys: ['↑', '↓'], label: 'Work items' },
      { keys: ['Esc'], label: 'Back' },
    ];
  }
  if (pathname === '/promotions') {
    return [
      { keys: ['↑', '↓'], label: 'Move' },
      { keys: ['Enter'], label: 'Open' },
      { keys: ['/'], label: 'Find promotion' },
      { keys: ['o'], label: 'Open in tracker' },
    ];
  }
  if (pathname === '/me/work-items' || pathname === '/my-tasks') {
    return [
      { keys: ['↑', '↓'], label: 'Move' },
      { keys: ['Enter'], label: 'Open' },
      { keys: ['a'], label: 'Assign' },
      { keys: ['o'], label: 'Open in tracker' },
    ];
  }
  if (pathname.startsWith('/deployments')) {
    return [
      { keys: ['↑', '↓'], label: 'Service' },
      { keys: ['←', '→'], label: 'Environment' },
      { keys: ['Enter'], label: 'Open' },
      { keys: ['/'], label: 'Find service' },
    ];
  }
  return [
    { keys: ['↑', '↓'], label: 'Move' },
    { keys: ['Enter'], label: 'Open' },
    { keys: ['/'], label: 'Search' },
  ];
}

export function KeyboardHints() {
  const { pathname } = useLocation();
  const hints = [...hintsForPath(pathname), ...GLOBAL_HINTS];

  return (
    <div className="px-1 pb-1 space-y-1" aria-label="Keyboard shortcuts for this page">
      {hints.map((hint) => (
        <div key={hint.label} className="flex items-center justify-between gap-2">
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
