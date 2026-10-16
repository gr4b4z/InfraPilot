import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { ArrowLeft, ArrowRight, Check, X, Info } from 'lucide-react';
import { useGuideStore } from '@/stores/guideStore';
import { visibleRect } from '@/lib/visibleRect';

/** Gap between the highlighted control and the tooltip, and between the tooltip and the viewport edge. */
const GAP = 12;
const TOOLTIP_WIDTH = 320;

interface Rect {
  top: number;
  left: number;
  width: number;
  height: number;
}

/**
 * Renders the running walkthrough: dims the page, rings the control the current step refers to,
 * and anchors a tooltip to it.
 *
 * The ring is pointer-events:none so the user can actually click the thing being pointed at — the
 * walkthrough demonstrates the real UI rather than replacing it with a simulation.
 */
export function GuideSpotlight() {
  const { plan, index, anchorMissing, next, back, stop, setAnchorMissing } = useGuideStore();
  const navigate = useNavigate();
  const location = useLocation();
  const [rect, setRect] = useState<Rect | null>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);

  const step = plan?.steps[index];
  const isLast = plan ? index === plan.steps.length - 1 : false;

  // A step may move the user to another page before it can point at anything.
  useEffect(() => {
    if (!step?.route) return;
    if (location.pathname === step.route) return;
    navigate(step.route);
  }, [step?.route, location.pathname, navigate]);

  // Locate the anchored element and follow it. The target can arrive late (data still loading) or
  // move (list re-renders, sticky headers), so this re-measures every frame rather than once.
  useLayoutEffect(() => {
    if (!plan || !step) return;

    const anchor = step.anchor;
    const selector = anchor ? `[data-guide-anchor="${CSS.escape(anchor)}"]` : null;
    let frame = 0;
    let settleAttempts = 0;

    const measure = () => {
      if (!selector) {
        // A step with no anchor is prose — centre the tooltip and stop looking.
        setRect((prev) => (prev === null ? prev : null));
        setAnchorMissing(false);
        return;
      }

      const el = document.querySelector<HTMLElement>(selector);

      if (!el) {
        setRect((prev) => (prev === null ? prev : null));
        // Give the page a moment to render before declaring the control absent — navigation and
        // data fetches both land after the step does.
        if (settleAttempts++ > 20) setAnchorMissing(true);
        return;
      }

      setAnchorMissing(false);
      settleAttempts = 0;
      // Clipped to the scroll area the control sits in. Scrolled out of view, the control gets no
      // ring hovering over the header; the tooltip centres itself until it is scrolled back.
      const visible = visibleRect(el);
      if (!visible) {
        setRect((prev) => (prev === null ? prev : null));
        return;
      }
      const r = visible.rect;
      // Only publish a genuinely different box. The loop runs every frame to track a moving
      // target, and a fresh object each time would re-render the overlay at 60fps for nothing.
      setRect((prev) =>
        prev && prev.top === r.top && prev.left === r.left && prev.width === r.width && prev.height === r.height
          ? prev
          : { top: r.top, left: r.left, width: r.width, height: r.height },
      );
    };

    if (selector) {
      document.querySelector<HTMLElement>(selector)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }

    const tick = () => {
      measure();
      frame = window.requestAnimationFrame(tick);
    };
    frame = window.requestAnimationFrame(tick);

    return () => window.cancelAnimationFrame(frame);
  }, [plan, step, index, setAnchorMissing, location.pathname]);

  // Esc leaves the walkthrough; arrows move through it. Bound only while a guide is running.
  useEffect(() => {
    if (!plan) return;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      // Never steal keys from something the user is typing into — a step may well be "type a reason here".
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
      if (target?.isContentEditable) return;

      if (e.key === 'Escape') {
        e.preventDefault();
        stop();
      } else if (e.key === 'ArrowRight') {
        e.preventDefault();
        next();
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault();
        back();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [plan, next, back, stop]);

  if (!plan || !step) return null;

  const tooltipStyle = computeTooltipPosition(rect);

  return (
    <div className="fixed inset-0 z-[70]" style={{ pointerEvents: 'none' }} aria-live="polite">
      {/* The ring. Its outsized spread doubles as the page dimmer, so there is exactly one element
          to keep in sync with the target's position. */}
      {rect && (
        <div
          className="absolute rounded-lg transition-all duration-200"
          style={{
            top: rect.top - 4,
            left: rect.left - 4,
            width: rect.width + 8,
            height: rect.height + 8,
            border: '2px solid var(--accent)',
            boxShadow: '0 0 0 9999px rgba(0, 0, 0, 0.45)',
            pointerEvents: 'none',
          }}
        />
      )}

      {/* No anchored control on this step — dim everything so the tooltip still reads as modal. */}
      {!rect && (
        <div
          className="absolute inset-0"
          style={{ backgroundColor: 'rgba(0, 0, 0, 0.45)', pointerEvents: 'none' }}
        />
      )}

      <div
        ref={tooltipRef}
        role="dialog"
        aria-label={`${plan.title} — step ${index + 1} of ${plan.steps.length}`}
        className="absolute rounded-xl border shadow-xl p-4"
        style={{
          ...tooltipStyle,
          width: `min(${TOOLTIP_WIDTH}px, calc(100vw - ${GAP * 2}px))`,
          backgroundColor: 'var(--bg-primary)',
          borderColor: 'var(--border-color)',
          pointerEvents: 'auto',
        }}
      >
        <div className="flex items-start justify-between gap-2 mb-2">
          <div className="min-w-0">
            <p
              className="text-[11px] font-medium uppercase tracking-wide truncate"
              style={{ color: 'var(--text-muted)' }}
            >
              {plan.title}
            </p>
            <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
              Step {index + 1} of {plan.steps.length}
            </p>
          </div>
          <button
            onClick={stop}
            aria-label="Close walkthrough"
            className="p-1 rounded-md shrink-0 transition-opacity hover:opacity-70"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={15} />
          </button>
        </div>

        <StepText text={step.text} />

        {step.note && (
          <div
            className="flex items-start gap-1.5 mt-2.5 px-2.5 py-2 rounded-lg text-[12px] leading-snug"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            <Info size={13} className="shrink-0 mt-0.5" />
            <span>{step.note}</span>
          </div>
        )}

        {anchorMissing && (
          <div
            className="mt-2.5 px-2.5 py-2 rounded-lg text-[12px] leading-snug"
            style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
          >
            This control is not on the page right now — it may need a role you do not have, or an
            earlier step to be finished first.
          </div>
        )}

        <div className="flex items-center gap-2 mt-3.5">
          <button
            onClick={back}
            disabled={index === 0}
            className="inline-flex items-center gap-1 px-2.5 py-1.5 rounded-lg text-[12px] font-medium transition-opacity"
            style={{ color: 'var(--text-muted)', opacity: index === 0 ? 0.4 : 1 }}
          >
            <ArrowLeft size={13} /> Back
          </button>
          <div className="flex-1" />
          <button
            onClick={next}
            className="inline-flex items-center gap-1 px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-90"
            style={{ backgroundColor: 'var(--accent)', color: 'var(--accent-fg)' }}
          >
            {isLast ? (
              <>
                <Check size={13} /> Done
              </>
            ) : (
              <>
                Next <ArrowRight size={13} />
              </>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Renders **bold** spans in step text. Guides lean on it to name the exact on-screen label. */
function StepText({ text }: { text: string }) {
  const parts = text.split(/(\*\*[^*]+\*\*)/g).filter(Boolean);
  return (
    <p className="text-[13px] leading-relaxed" style={{ color: 'var(--text-primary)' }}>
      {parts.map((part, i) =>
        part.startsWith('**') && part.endsWith('**') ? (
          <strong key={i} style={{ color: 'var(--text-primary)' }}>
            {part.slice(2, -2)}
          </strong>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </p>
  );
}

/**
 * Places the tooltip below the target, above it when the lower half is short on room, and centred
 * when there is no target at all. Horizontal position is clamped so it never leaves the viewport on
 * a narrow screen.
 */
function computeTooltipPosition(rect: Rect | null): { top: number; left: number } {
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  const width = Math.min(TOOLTIP_WIDTH, vw - GAP * 2);
  // Tall enough for the common case; the tooltip may exceed it, which only costs a few pixels of
  // overlap at the very bottom of a short viewport.
  const assumedHeight = 190;

  if (!rect) {
    return {
      top: Math.max(GAP, vh / 2 - assumedHeight / 2),
      left: Math.max(GAP, vw / 2 - width / 2),
    };
  }

  const below = rect.top + rect.height + GAP;
  const fitsBelow = below + assumedHeight <= vh - GAP;
  const top = fitsBelow ? below : Math.max(GAP, rect.top - assumedHeight - GAP);

  const preferredLeft = rect.left + rect.width / 2 - width / 2;
  const left = Math.min(Math.max(GAP, preferredLeft), vw - width - GAP);

  return { top, left };
}
