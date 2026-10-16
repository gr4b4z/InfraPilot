import { useLocation, useNavigate } from 'react-router-dom';
import { ArrowUpRight, Check, Crosshair } from 'lucide-react';
import { useHighlightStore, type HighlightTarget } from '@/stores/highlightStore';

/**
 * Records that the assistant moved the user or ringed something for them, and does it again on
 * click if they have since wandered off or dismissed the rings.
 *
 * Navigation happens the moment the turn lands, so by the time this renders the user is usually
 * already there — hence the past tense and the tick. It earns its place on the second read of a
 * thread, when the same chip is the way back to a page discussed ten messages ago, rings included.
 */
export function NavigationChip({
  route,
  label,
  highlights,
}: {
  /** Where the assistant took the user. Absent when it only ringed things on the current page. */
  route?: string;
  label: string;
  highlights?: HighlightTarget[];
}) {
  const navigate = useNavigate();
  const location = useLocation();
  const show = useHighlightStore((s) => s.show);

  const target = route ?? location.pathname;
  const here = target === location.pathname + location.search || target === location.pathname;

  const activate = () => {
    if (route && !here) navigate(route);
    if (highlights && highlights.length > 0) show(highlights, target.split('?')[0]);
  };

  const text = !route ? 'Show on page' : here ? `Showing ${label}` : `Back to ${label}`;
  const Icon = !route ? Crosshair : here ? Check : ArrowUpRight;

  return (
    <button
      type="button"
      onClick={activate}
      className="inline-flex items-center gap-1.5 mt-2 px-2.5 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
      style={{
        backgroundColor: 'var(--bg-secondary)',
        color: here && route ? 'var(--text-muted)' : 'var(--accent)',
        border: '1px solid var(--border-color)',
      }}
    >
      <Icon size={12} />
      <span className="truncate max-w-[220px]">{text}</span>
    </button>
  );
}
