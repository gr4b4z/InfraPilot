import { useEffect, useState } from 'react';
import { CheckCheck, Loader2 } from 'lucide-react';
import { api } from '@/lib/api';
import type {
  ApproveAllWorkItemRow,
  ApproveAllWorkItemsResult,
  PromotionCandidate,
  PromotionSourceEventReference,
  WorkItemInstanceState,
} from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { instanceStateStyle } from '@/lib/workItem';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';

/**
 * "Approve all" for the promotion page's Work items card: signs off, in one go, every work item of
 * the promotion still waiting for a decision (`POST /api/promotions/{id}/work-items/approvals`) —
 * for the QA reviewer who has checked the whole bundle and would otherwise open each item in turn.
 *
 * The confirmation names exactly what is about to be approved, by key and title, from a dry run
 * taken when the button is pressed; the apply sends those keys back, so nothing the reviewer did not
 * see gets their name on it. Items already approved, or carrying an issue or a block, are listed as
 * left alone — an issue or a block is somebody's deliberate call, and this does not overrule it.
 *
 * Work-item sign-off is the QA role's jurisdiction (Admin included), so the control only appears for
 * those users — never on a read-only (terminal) promotion, nor on an auto-approve one, which has no
 * sign-off to give.
 */
export function ApproveAllWorkItems({
  candidate,
  workItems,
  readOnly,
  requiresApproval,
  onChanged,
}: {
  candidate: PromotionCandidate;
  workItems: PromotionSourceEventReference[];
  readOnly: boolean;
  /** False for an auto-approve promotion: no human gate, so no work-item sign-off either. */
  requiresApproval: boolean;
  /** Called after a write that signed something off, so the page refetches the candidate. */
  onChanged: () => void;
}) {
  const user = useAuthStore((s) => s.user);
  const [preview, setPreview] = useState<ApproveAllWorkItemsResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  // A receipt, not a banner: it says what the write did and then gets out of the way.
  useEffect(() => {
    if (status?.tone !== 'ok') return;
    const timer = setTimeout(() => setStatus(null), 6000);
    return () => clearTimeout(timer);
  }, [status]);

  if (readOnly || !requiresApproval || workItems.length === 0 || !(user?.isQA || user?.isAdmin)) {
    return null;
  }

  const open = async () => {
    setLoading(true);
    setStatus(null);
    setError(null);
    try {
      const res = await api.approveAllPromotionWorkItems(candidate.id, { dryRun: true });
      if (res.approved.length === 0) {
        setStatus({ tone: 'ok', text: 'Nothing to approve — every work item already has a decision' });
      } else {
        setPreview(res);
      }
    } catch (err) {
      setStatus({ tone: 'error', text: err instanceof Error ? err.message : 'Failed to load the work items' });
    } finally {
      setLoading(false);
    }
  };

  const confirm = async (comment: string) => {
    if (!preview) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.approveAllPromotionWorkItems(candidate.id, {
        dryRun: false,
        keys: preview.approved.map((i) => i.workItemKey),
        comment: comment || null,
      });
      setPreview(null);
      const parts: string[] = [];
      if (res.approved.length > 0) parts.push(`Approved ${countLabel(res.approved.length)}`);
      if (res.failed.length > 0) {
        parts.push(
          `${countLabel(res.failed.length)} could not be approved: ${res.failed
            .map((f) => `${f.workItemKey} (${f.error ?? 'refused'})`)
            .join(', ')}`,
        );
      }
      setStatus({
        tone: res.failed.length > 0 ? 'error' : 'ok',
        text: parts.join(' · ') || 'Nothing changed — the work items were decided in the meantime',
      });
      if (res.approved.length > 0) onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to approve the work items');
    } finally {
      setBusy(false);
    }
  };

  const count = preview?.approved.length ?? 0;

  return (
    <span className="inline-flex items-center gap-2">
      {status && (
        <span
          className="text-[11px]"
          role="status"
          style={{ color: status.tone === 'ok' ? 'var(--success)' : 'var(--danger)' }}
        >
          {status.text}
        </span>
      )}
      <button
        type="button"
        onClick={open}
        disabled={loading || busy}
        data-guide-anchor="promotion-approve-all-work-items"
        className="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-[11px] font-medium transition-opacity hover:opacity-80 disabled:opacity-60"
        style={{ border: '1px dashed var(--border-color)', color: 'var(--text-secondary)' }}
        title="Sign off every work item of this promotion that is still waiting for a decision"
      >
        {loading ? <Loader2 size={11} className="animate-spin" /> : <CheckCheck size={11} />} Approve all
      </button>
      {preview && (
        <ConfirmDialog
          title={`Approve ${countLabel(count)}?`}
          confirmLabel={`Approve ${countLabel(count)}`}
          confirmTone="success"
          commentLabel="Comment (optional) — recorded on each sign-off"
          busy={busy}
          error={error}
          onConfirm={confirm}
          onCancel={() => {
            setPreview(null);
            setError(null);
          }}
          body={
            <div className="space-y-3">
              <p>
                Each of these is signed off in your name, as if you had approved it on its own page.
              </p>
              <WorkItemList rows={preview.approved} showState={(s) => s === 'Bypassed'} />
              {preview.unchanged.length > 0 && (
                <div className="space-y-1.5">
                  <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    Left as {preview.unchanged.length === 1 ? 'it is' : 'they are'} — already decided:
                  </p>
                  <WorkItemList rows={preview.unchanged} showState={() => true} muted />
                </div>
              )}
            </div>
          }
        />
      )}
    </span>
  );
}

/** Key, title and — where it says something — state of each work item, one per line. */
function WorkItemList({
  rows,
  showState,
  muted = false,
}: {
  rows: ApproveAllWorkItemRow[];
  showState: (state: ApproveAllWorkItemRow['state']) => boolean;
  muted?: boolean;
}) {
  return (
    <ul
      className="max-h-56 overflow-y-auto rounded-lg border divide-y text-[12px]"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      {rows.map((row) => {
        const style = row.state === 'Unknown' ? null : instanceStateStyle(row.state as WorkItemInstanceState);
        return (
          <li
            key={row.workItemKey}
            className="flex items-center gap-2 px-2.5 py-1.5"
            style={{ borderColor: 'var(--border-color)', opacity: muted ? 0.75 : 1 }}
          >
            <span className="font-mono shrink-0" style={{ color: 'var(--text-primary)' }}>
              {row.workItemKey}
            </span>
            <span className="truncate min-w-0 flex-1" style={{ color: 'var(--text-secondary)' }} title={row.title ?? undefined}>
              {row.title ?? ''}
            </span>
            {style && showState(row.state) && (
              <span className="badge shrink-0" style={{ backgroundColor: style.bg, color: style.color }}>
                {style.label}
              </span>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function countLabel(n: number) {
  return n === 1 ? '1 work item' : `${n} work items`;
}
