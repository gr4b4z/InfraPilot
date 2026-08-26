import { useRef, useState, type ReactNode } from 'react';
import { Info } from 'lucide-react';
import { AnchoredPopover } from './AnchoredPopover';

export interface InfoContent {
  /** One-sentence definition: what this number is. */
  what: ReactNode;
  /** How it is computed — source, window, exclusions. Quote live `definition` values from the
   *  API response here rather than restating them, so the explanation can't drift from what the
   *  backend actually counted. */
  how: ReactNode;
  /** What decision it supports and when it should worry the reader. */
  why: ReactNode;
}

/**
 * The ⓘ affordance on KPI tiles and analytics sections: click → a three-part explainer
 * (what / how it's computed / why it matters). A click-popover rather than a hover-tooltip on
 * purpose — the content is a few sentences, and hover doesn't exist on touch or in a screen-share
 * where someone else drives. Always visible (not hover-revealed) for the same reason.
 */
export function InfoPopover({ label, content }: { label: string; content: InfoContent }) {
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLButtonElement>(null);

  return (
    <>
      <button
        ref={anchorRef}
        onClick={(e) => {
          // Tiles and section headers may themselves be clickable — the explainer must not
          // trigger them.
          e.stopPropagation();
          setOpen((v) => !v);
        }}
        aria-expanded={open}
        aria-label={`About: ${label}`}
        className="inline-flex p-0.5 rounded transition-opacity hover:opacity-100"
        style={{ color: 'var(--text-muted)', opacity: 0.6 }}
      >
        <Info size={13} />
      </button>
      {open && (
        <AnchoredPopover
          anchorRef={anchorRef}
          onClose={() => setOpen(false)}
          width={340}
          ariaLabel={`About: ${label}`}
          className="rounded-xl border p-4 shadow-lg"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
        >
          <div className="space-y-3 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
            <div>
              <p className="font-semibold text-[12px] mb-0.5" style={{ color: 'var(--text-primary)' }}>
                {label}
              </p>
              <div>{content.what}</div>
            </div>
            <div>
              <p
                className="text-[10px] font-medium uppercase tracking-wider mb-0.5"
                style={{ color: 'var(--text-muted)' }}
              >
                How it's computed
              </p>
              <div>{content.how}</div>
            </div>
            <div>
              <p
                className="text-[10px] font-medium uppercase tracking-wider mb-0.5"
                style={{ color: 'var(--text-muted)' }}
              >
                Why it matters
              </p>
              <div>{content.why}</div>
            </div>
          </div>
        </AnchoredPopover>
      )}
    </>
  );
}
