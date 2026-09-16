import { useEffect, useLayoutEffect, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { X } from 'lucide-react';
import { useHighlightStore, type HighlightTarget } from '@/stores/highlightStore';
import { useGuideStore } from '@/stores/guideStore';

interface Ring {
  anchor: string;
  label?: string;
  top: number;
  left: number;
  width: number;
  height: number;
}

/** Frames to wait for an anchor to appear before giving up on it — data and navigation both land late. */
const SETTLE_FRAMES = 90;

/**
 * Draws the assistant's rings: an accent outline around every element it referred to, with a small
 * caption. Not modal — the page is neither dimmed nor blocked, because these accompany an answer
 * rather than interrupt with one. The staging cell the assistant just quoted should simply be
 * findable at a glance while the user reads.
 *
 * Positions are re-measured every frame while rings are shown, the same way the walkthrough
 * spotlight does it: targets arrive late (a fetch still in flight) and move (a list re-sorts).
 */
export function HighlightLayer() {
  const { targets, route, arrived, arrive, dismiss, clear } = useHighlightStore();
  const guideRunning = useGuideStore((s) => s.plan !== null);
  const location = useLocation();
  const [rings, setRings] = useState<Ring[]>([]);

  const onRoute = !route || route === location.pathname;

  // Rings belong to one page. They wait for the user to reach it — the assistant's navigation may
  // still be in flight when they are set — and are cleared once the user leaves it again. A
  // destination that is never reached would otherwise keep them pending forever, hence the timeout.
  useEffect(() => {
    if (targets.length === 0) return;
    if (onRoute) {
      if (!arrived) arrive();
      return;
    }
    if (arrived) {
      clear();
      return;
    }
    const timer = window.setTimeout(clear, 15_000);
    return () => window.clearTimeout(timer);
  }, [targets.length, onRoute, arrived, arrive, clear]);

  useLayoutEffect(() => {
    let frame = 0;

    if (targets.length === 0 || guideRunning || !onRoute) {
      // Nothing to draw. Cleared from a frame callback rather than the effect body, which is the
      // same place every other ring update comes from.
      frame = window.requestAnimationFrame(() => setRings((prev) => (prev.length === 0 ? prev : [])));
      return () => window.cancelAnimationFrame(frame);
    }

    let elapsed = 0;
    let scrolled = false;

    const measure = () => {
      elapsed++;
      const next: Ring[] = [];
      for (const target of targets) {
        for (const el of findAll(target)) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 && r.height === 0) continue;
          next.push({ anchor: target.anchor, label: target.label, top: r.top, left: r.left, width: r.width, height: r.height });
        }
      }

      // Bring the first ringed element into view once, when it first exists — not on every frame,
      // which would fight the user's own scrolling.
      if (!scrolled && next.length > 0) {
        scrolled = true;
        const first = findAll(targets.find((t) => t.anchor === next[0].anchor)!)[0];
        first?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }

      // Nothing found after a generous wait: the anchors are not on this page. Fall silent rather
      // than leave an empty layer polling forever.
      if (!scrolled && elapsed > SETTLE_FRAMES) {
        window.cancelAnimationFrame(frame);
        return;
      }

      // Publish only real changes so the loop does not re-render at 60fps for a still page.
      setRings((prev) => (sameRings(prev, next) ? prev : next));
      frame = window.requestAnimationFrame(measure);
    };

    frame = window.requestAnimationFrame(measure);
    return () => window.cancelAnimationFrame(frame);
  }, [targets, guideRunning, onRoute, location.pathname]);

  // Escape clears the rings — bound in KeyboardLayer, which owns Escape app-wide and treats the
  // rings as an overlay with first claim on it, ahead of its "go back" fallback.

  if (rings.length === 0) return null;

  // One caption per anchor, on its first ring: a gate list with three Approve buttons gets three
  // rings and one label, not three labels saying the same thing.
  const captioned = new Set<string>();

  return (
    <div className="fixed inset-0 z-[60]" style={{ pointerEvents: 'none' }} aria-live="polite">
      {rings.map((ring, i) => {
        const showLabel = !!ring.label && !captioned.has(ring.anchor);
        if (showLabel) captioned.add(ring.anchor);
        const labelAbove = ring.top > 36;

        return (
          <div key={`${ring.anchor}-${i}`}>
            <div
              className="absolute rounded-lg assistant-ring"
              style={{
                top: ring.top - 4,
                left: ring.left - 4,
                width: ring.width + 8,
                height: ring.height + 8,
                border: '2px solid var(--accent)',
                boxShadow: '0 0 0 4px var(--accent-muted, rgba(0,0,0,0.08))',
                pointerEvents: 'none',
              }}
            />
            {showLabel && (
              <div
                role="status"
                className="absolute inline-flex items-center gap-1 pl-2 pr-1 py-0.5 rounded-md text-[11px] font-medium shadow-md whitespace-nowrap"
                style={{
                  top: labelAbove ? ring.top - 30 : ring.top + ring.height + 8,
                  left: Math.max(8, Math.min(ring.left - 4, window.innerWidth - 260)),
                  backgroundColor: 'var(--accent)',
                  color: 'var(--accent-fg)',
                  pointerEvents: 'auto',
                  maxWidth: 250,
                }}
              >
                <span className="truncate">{ring.label}</span>
                <button
                  type="button"
                  onClick={() => dismiss(ring.anchor)}
                  aria-label={`Remove highlight: ${ring.label}`}
                  className="inline-flex p-0.5 rounded hover:opacity-70"
                  style={{ color: 'var(--accent-fg)' }}
                >
                  <X size={11} />
                </button>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function findAll(target: HighlightTarget): HTMLElement[] {
  return [...document.querySelectorAll<HTMLElement>(`[data-guide-anchor="${CSS.escape(target.anchor)}"]`)];
}

function sameRings(a: Ring[], b: Ring[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    const x = a[i];
    const y = b[i];
    if (x.anchor !== y.anchor || x.top !== y.top || x.left !== y.left || x.width !== y.width || x.height !== y.height) {
      return false;
    }
  }
  return true;
}
