import { useState, type ReactNode } from 'react';
import { AlertTriangle, Check, Copy } from 'lucide-react';
import { copyText } from '@/lib/diagnostics';

/**
 * The shared look of "something is wrong" — startup failures, render crashes, a page that blew
 * up. Each one says what happened in plain words, offers the action most likely to fix it, and
 * carries a "Copy details" report so the next bug report says more than "white screen".
 */

export function ActionButton({
  onClick,
  primary = false,
  disabled = false,
  compact = false,
  children,
}: {
  onClick: () => void;
  primary?: boolean;
  disabled?: boolean;
  /** Banner-sized. */
  compact?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`inline-flex items-center gap-1.5 font-medium border transition-colors disabled:opacity-50 disabled:cursor-default ${
        compact ? 'px-2.5 py-1 text-[12px] rounded-md' : 'px-3.5 py-2 text-[13px] rounded-lg'
      } ${primary ? '' : 'hover:bg-[var(--accent-muted)]'}`}
      style={
        primary
          ? { backgroundColor: 'var(--accent)', borderColor: 'var(--accent)', color: 'var(--accent-fg)' }
          : { borderColor: 'var(--border-color)', color: 'var(--text-primary)' }
      }
    >
      {children}
    </button>
  );
}

export function CopyDetailsButton({ details, compact = false }: { details: () => string; compact?: boolean }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');
  return (
    <ActionButton
      compact={compact}
      onClick={() => void copyText(details()).then((ok) => setState(ok ? 'copied' : 'failed'))}
    >
      {state === 'copied' ? <Check size={compact ? 12 : 14} /> : <Copy size={compact ? 12 : 14} />}
      {state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy failed — use the details below' : 'Copy details'}
    </ActionButton>
  );
}

function TechnicalDetails({ details }: { details: () => string }) {
  // Rendered on open, so the report's timestamp is when it was read rather than when it crashed.
  const [open, setOpen] = useState(false);
  return (
    <details onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="cursor-pointer text-[12px]" style={{ color: 'var(--text-muted)' }}>
        Technical details
      </summary>
      {open && (
        <pre
          className="mt-2 p-3 rounded-lg text-[11px] leading-relaxed whitespace-pre-wrap break-words max-h-72 overflow-auto select-all"
          style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-secondary)', fontFamily: 'var(--font-mono)' }}
        >
          {details()}
        </pre>
      )}
    </details>
  );
}

export interface ProblemCardProps {
  title: string;
  message: ReactNode;
  /** Builds the diagnostics text; called on demand. */
  details: () => string;
  /** Buttons ahead of "Copy details", most useful first. */
  actions?: ReactNode;
  /** Small print under the details. */
  footer?: ReactNode;
}

/** The problem panel on its own, for use inside the shell. */
export function ProblemCard({ title, message, details, actions, footer }: ProblemCardProps) {
  return (
    <div
      role="alert"
      className="w-full max-w-xl rounded-xl border p-6 sm:p-8 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex items-start gap-3">
        <AlertTriangle size={20} className="shrink-0 mt-0.5" style={{ color: 'var(--warning)' }} />
        <div className="space-y-1.5 min-w-0">
          <h1 className="text-[16px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            {title}
          </h1>
          <div className="text-[13px] space-y-1.5" style={{ color: 'var(--text-secondary)' }}>
            {message}
          </div>
        </div>
      </div>
      <div className="flex flex-wrap gap-2">
        {actions}
        <CopyDetailsButton details={details} />
      </div>
      <TechnicalDetails details={details} />
      {footer && (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          {footer}
        </p>
      )}
    </div>
  );
}

/** The problem panel filling the window, for when there is no shell to show it in. */
export function ProblemScreen(props: ProblemCardProps) {
  return (
    <div
      className="flex items-center justify-center min-h-screen p-4 sm:p-6"
      style={{ backgroundColor: 'var(--bg-secondary)' }}
    >
      <ProblemCard {...props} />
    </div>
  );
}
