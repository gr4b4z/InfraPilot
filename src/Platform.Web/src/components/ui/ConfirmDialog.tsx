import { useRef, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';

/**
 * Confirmation step for an irreversible decision.
 *
 * Approving or rejecting a promotion releases (or refuses) a deploy, and once `A` and `R` exist as
 * single keystrokes a mistyped letter must not be able to do that. The dialog is the stop: it names
 * what is about to happen to which promotion, and requires a second, deliberate action.
 *
 * Keyboard first, because the shortcut that opens it is. The confirm button takes focus, so Enter
 * confirms and Escape cancels without touching the comment field — but Tab reaches the comment for
 * anyone who wants to leave one, and Enter inside a textarea inserts a newline rather than
 * submitting, which is what a multi-line field should do.
 *
 * A comment can be required (`commentRequired`), which is how rejection works: telling someone their
 * change was refused without saying why is not a decision anyone can act on.
 */
export function ConfirmDialog({
  title,
  body,
  confirmLabel,
  confirmTone = 'accent',
  commentLabel,
  commentRequired = false,
  busy = false,
  error,
  onConfirm,
  onCancel,
}: {
  title: string;
  body: React.ReactNode;
  confirmLabel: string;
  /** Which palette the confirm button carries. `danger` for refusals and destructive actions. */
  confirmTone?: 'accent' | 'danger' | 'success';
  /** Show a comment field with this label. Omit for no comment. */
  commentLabel?: string;
  commentRequired?: boolean;
  busy?: boolean;
  error?: string | null;
  onConfirm: (comment: string) => void;
  onCancel: () => void;
}) {
  const [comment, setComment] = useState('');
  const confirmRef = useRef<HTMLButtonElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const commentRef = useRef<HTMLTextAreaElement>(null);

  const trimmed = comment.trim();
  const blocked = busy || (commentRequired && trimmed.length === 0);

  const toneFill =
    confirmTone === 'danger'
      ? 'var(--danger-solid)'
      : confirmTone === 'success'
        ? 'var(--success-solid)'
        : 'var(--accent)';

  return (
    <Dialog
      onClose={onCancel}
      ariaLabel={title}
      width={480}
      // Focus the comment when one is required — there is nothing to confirm until it's written, so
      // landing on a disabled button would be a dead end.
      initialFocusRef={commentRequired ? commentRef : confirmRef}
    >
      <div className="px-4 py-3 border-b" style={{ borderColor: 'var(--border-color)' }}>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          {title}
        </h2>
      </div>

      <div className="px-4 py-3 space-y-3">
        <div className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
          {body}
        </div>

        {commentLabel && (
          <label className="block">
            <span className="block text-[12px] mb-1" style={{ color: 'var(--text-muted)' }}>
              {commentLabel}
              {commentRequired && <span style={{ color: 'var(--danger)' }}> *</span>}
            </span>
            <textarea
              ref={commentRef}
              value={comment}
              onChange={(e) => setComment(e.target.value)}
              rows={3}
              className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none outline-none focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
                color: 'var(--text-primary)',
              }}
              disabled={busy}
            />
          </label>
        )}

        {error && (
          <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
            {error}
          </p>
        )}
      </div>

      <div
        className="px-4 py-3 border-t flex items-center justify-between gap-3"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          ←→ to choose · Enter to confirm · Esc to cancel
        </span>
        {/* Arrows move between Cancel and Confirm without activating either — this is a decision, so
            landing on a choice must not be the same as making it. That rules out RovingGroup, whose
            focus-follows-selection is right for filters and wrong here. */}
        <div
          className="flex items-center gap-2"
          onKeyDown={(event) => {
            if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
            const buttons = [cancelRef.current, confirmRef.current].filter(
              (b): b is HTMLButtonElement => b !== null && !b.disabled,
            );
            const current = buttons.indexOf(document.activeElement as HTMLButtonElement);
            if (buttons.length === 0) return;
            event.preventDefault();
            const forward = event.key === 'ArrowRight' || event.key === 'ArrowDown';
            // From outside the pair (focus still in the comment box) the first arrow lands on an end
            // rather than jumping by an index nobody chose.
            const next = current === -1
              ? (forward ? 0 : buttons.length - 1)
              : Math.max(0, Math.min(current + (forward ? 1 : -1), buttons.length - 1));
            buttons[next].focus();
          }}
        >
          <button
            ref={cancelRef}
            type="button"
            onClick={onCancel}
            disabled={busy}
            className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80 disabled:opacity-50"
            style={{ color: 'var(--text-secondary)', backgroundColor: 'var(--bg-secondary)' }}
          >
            Cancel
          </button>
          <button
            ref={confirmRef}
            type="button"
            onClick={() => onConfirm(trimmed)}
            disabled={blocked}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: toneFill }}
          >
            {busy && <Loader2 size={12} className="animate-spin" />}
            {confirmLabel}
          </button>
        </div>
      </div>
    </Dialog>
  );
}
