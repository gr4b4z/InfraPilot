import { Compass, Play, RotateCcw } from 'lucide-react';
import { useGuideStore, type GuidePlan } from '@/stores/guideStore';

/**
 * The chat's record of a walkthrough the agent started. The steps stay readable here after the
 * spotlight is dismissed, and "Show me again" replays it — a user who skimmed the overlay and then
 * lost their place should not have to re-ask the question.
 */
export function GuideCard({ plan }: { plan: GuidePlan }) {
  const start = useGuideStore((s) => s.start);
  const running = useGuideStore((s) => s.plan?.id === plan.id);

  return (
    <div
      className="rounded-xl border overflow-hidden mt-2"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-start gap-2 px-3 py-2.5">
        <Compass size={14} className="shrink-0 mt-0.5" style={{ color: 'var(--accent)' }} />
        <div className="min-w-0 flex-1">
          <p className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            {plan.title}
          </p>
          <p className="text-[12px] leading-snug mt-0.5" style={{ color: 'var(--text-muted)' }}>
            {plan.summary}
          </p>
        </div>
      </div>

      <ol className="px-3 pb-2 space-y-1.5">
        {plan.steps.map((step, i) => (
          <li key={i} className="flex gap-2 text-[12px] leading-snug">
            <span
              className="shrink-0 w-[18px] h-[18px] rounded-full grid place-items-center text-[10px] font-medium"
              style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}
            >
              {i + 1}
            </span>
            <span style={{ color: 'var(--text-secondary, var(--text-primary))' }}>
              {stripEmphasis(step.text)}
            </span>
          </li>
        ))}
      </ol>

      <div className="px-3 pb-3">
        <button
          onClick={() => start(plan)}
          className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)', color: 'var(--accent-fg)' }}
        >
          {running ? <RotateCcw size={12} /> : <Play size={12} />}
          {running ? 'Restart walkthrough' : 'Show me again'}
        </button>
      </div>
    </div>
  );
}

/** The chat list renders plain text; **emphasis** belongs to the spotlight tooltip. */
function stripEmphasis(text: string) {
  return text.replace(/\*\*([^*]+)\*\*/g, '$1');
}
