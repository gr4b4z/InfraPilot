import { useState, type ReactNode } from 'react';
import { ChevronDown, ChevronRight, Snowflake } from 'lucide-react';

/**
 * Shell of the Promotion Freeze banners (deployments and promotions pages): one line — snowflake,
 * title, `action` on the right — that expands to `children`. Collapsed on every mount; the expanded
 * state is not remembered.
 */
export function CollapsibleFreezeBanner({
  title,
  action,
  guideAnchor,
  children,
}: {
  title: string;
  /** Right-hand control of the collapsed line, e.g. "Manage freezes". Kept outside the toggle. */
  action?: ReactNode;
  guideAnchor?: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const Chevron = open ? ChevronDown : ChevronRight;
  return (
    <div
      data-guide-anchor={guideAnchor}
      className="rounded-xl border px-3 py-2"
      style={{ borderColor: 'var(--info)', backgroundColor: 'var(--info-bg)', borderLeft: '3px solid var(--info)' }}
    >
      <div className="flex items-center gap-3">
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          className="flex-1 min-w-0 flex items-center gap-2.5 text-left rounded-md"
        >
          <Chevron size={14} style={{ color: 'var(--info)', flexShrink: 0 }} />
          <Snowflake size={16} style={{ color: 'var(--info)', flexShrink: 0 }} />
          <span className="text-[13px] font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
            {title}
          </span>
        </button>
        {action}
      </div>
      {open && <div className="pt-2.5 pb-0.5 pl-[26px] space-y-1.5">{children}</div>}
    </div>
  );
}
