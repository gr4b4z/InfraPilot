import { useLocation, useNavigate } from 'react-router-dom';
import { ArrowUpRight, Check } from 'lucide-react';

/**
 * Records that the assistant moved the user, and takes them back if they have since wandered off.
 *
 * Navigation happens the moment the turn lands, so by the time this renders the user is usually
 * already there — hence the past tense and the tick. It earns its place on the second read of a
 * thread, when the same chip is the way back to a page discussed ten messages ago.
 */
export function NavigationChip({ route, label }: { route: string; label: string }) {
  const navigate = useNavigate();
  const location = useLocation();

  const here = route === location.pathname + location.search || route === location.pathname;

  return (
    <button
      type="button"
      onClick={() => navigate(route)}
      className="inline-flex items-center gap-1.5 mt-2 px-2.5 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
      style={{
        backgroundColor: 'var(--bg-secondary)',
        color: here ? 'var(--text-muted)' : 'var(--accent)',
        border: '1px solid var(--border-color)',
      }}
    >
      {here ? <Check size={12} /> : <ArrowUpRight size={12} />}
      <span className="truncate max-w-[220px]">{here ? `Showing ${label}` : `Back to ${label}`}</span>
    </button>
  );
}
