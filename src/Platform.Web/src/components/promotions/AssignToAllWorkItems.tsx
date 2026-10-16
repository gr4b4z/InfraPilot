import { useEffect, useRef, useState } from 'react';
import { Users } from 'lucide-react';
import { api } from '@/lib/api';
import type { PromotionCandidate, PromotionSourceEventReference } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { roleDisplay, useResolveRoleKey } from '@/lib/roleLabel';
import { InlineUserPicker } from '@/components/promotions/WorkItemParticipants';

/**
 * "Assign to all" for the promotion page's Work items card: one person into one role on every work
 * item of the promotion, in one write (`PATCH /api/promotions/{id}/work-items/participants`).
 *
 * The assignment is made on each work item — the same slot its own Assign control writes — so it
 * replaces whoever a ticket already had in the role (an alias like Jira's `qa` included). That is
 * the difference from a promotion-level participant, which only fills tickets that have nobody and
 * so can't correct one that arrived with the wrong person. "Only the ones without" is offered for
 * the fill-the-gaps case.
 *
 * Work-item management is the QA role's jurisdiction (Admin included), so the control only appears
 * for those users, and never on a read-only (terminal) promotion.
 */
export function AssignToAllWorkItems({
  candidate,
  workItems,
  readOnly,
  onChanged,
}: {
  candidate: PromotionCandidate;
  workItems: PromotionSourceEventReference[];
  readOnly: boolean;
  /** Called after a write that changed something, so the page refetches the candidate. */
  onChanged: () => void;
}) {
  const user = useAuthStore((s) => s.user);
  const resolve = useResolveRoleKey();
  const anchorRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [onlyMissing, setOnlyMissing] = useState(false);
  const [status, setStatus] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  // A receipt, not a banner: it says what the write did and then gets out of the way.
  useEffect(() => {
    if (status?.tone !== 'ok') return;
    const timer = setTimeout(() => setStatus(null), 6000);
    return () => clearTimeout(timer);
  }, [status]);

  if (readOnly || workItems.length === 0 || !(user?.isQA || user?.isAdmin)) return null;

  // Whether a work item already has somebody reachable in `role`, judged the way the server judges
  // it: the ticket's own first participant in the role wins, otherwise a promotion-level one fills it.
  const hasSomebody = (wi: PromotionSourceEventReference, role: string) => {
    const key = resolve(role);
    const own = (wi.participants ?? []).find((p) => resolve(p.role) === key);
    if (own) return !!own.email?.trim();
    return candidate.participants.some((p) => resolve(p.role) === key && !!p.email?.trim());
  };

  // Open on the role the policy is asking for, when there is one — that is what this is usually for.
  const initialRole =
    candidate.workItemRoleGaps?.[0]?.missingRoles?.[0] ?? candidate.requiredWorkItemRoles?.[0] ?? '';

  const submit = async (picked: { role: string; email: string; displayName: string }) => {
    setBusy(true);
    setStatus(null);
    try {
      const res = await api.assignPromotionWorkItemsParticipant(
        candidate.id,
        picked.role,
        { email: picked.email, displayName: picked.displayName },
        onlyMissing,
      );
      setOpen(false);
      const roleLabel = roleDisplay({ role: res.role });
      const who = picked.displayName || picked.email;
      const parts = [
        res.updated.length > 0
          ? `${who} is now ${roleLabel} on ${countLabel(res.updated.length)}`
          : `Nothing to change — ${who} was already ${roleLabel} where it applies`,
      ];
      if (res.updated.length > 0 && res.unchanged.length > 0) parts.push(`${res.unchanged.length} already were`);
      if (res.skipped.length > 0) parts.push(`${res.skipped.length} already had a ${roleLabel}`);
      setStatus({ tone: 'ok', text: parts.join(' · ') });
      if (res.updated.length > 0) onChanged();
    } catch (err) {
      setStatus({ tone: 'error', text: err instanceof Error ? err.message : 'Failed to assign' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <span className="inline-flex items-center gap-2 relative">
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
        ref={anchorRef}
        type="button"
        onClick={() => setOpen((v) => !v)}
        disabled={busy}
        data-guide-anchor="promotion-assign-all-work-items"
        className="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-[11px] font-medium transition-opacity hover:opacity-80 disabled:opacity-60"
        style={{ border: '1px dashed var(--border-color)', color: 'var(--text-secondary)' }}
        title="Put one person in a role on every work item of this promotion"
      >
        <Users size={11} /> Assign to all
      </button>
      {open && (
        <InlineUserPicker
          anchorRef={anchorRef}
          align="right"
          role={null}
          initialRole={initialRole}
          heading={`Assign on all ${countLabel(workItems.length)}`}
          busy={busy}
          onCancel={() => setOpen(false)}
          onPick={submit}
          renderOptions={(role) => {
            const without = role ? workItems.filter((wi) => !hasSomebody(wi, role)).length : null;
            const label = role ? roleDisplay({ role }) : 'this role';
            return (
              <fieldset className="mt-2 px-1 space-y-1" disabled={busy}>
                <legend className="sr-only">Which work items</legend>
                <ScopeOption
                  checked={!onlyMissing}
                  onSelect={() => setOnlyMissing(false)}
                  label={`All ${countLabel(workItems.length)}`}
                  hint={`Replaces anyone already in ${label}`}
                />
                <ScopeOption
                  checked={onlyMissing}
                  onSelect={() => setOnlyMissing(true)}
                  label={
                    without === null
                      ? `Only those without ${label}`
                      : `Only the ${without} without ${label}`
                  }
                  hint="Leaves existing assignments alone"
                />
              </fieldset>
            );
          }}
        />
      )}
    </span>
  );
}

function ScopeOption({
  checked,
  onSelect,
  label,
  hint,
}: {
  checked: boolean;
  onSelect: () => void;
  label: string;
  hint: string;
}) {
  return (
    <label className="flex items-start gap-2 cursor-pointer text-[12px]" style={{ color: 'var(--text-primary)' }}>
      <input
        type="radio"
        name="assign-all-scope"
        checked={checked}
        onChange={onSelect}
        className="mt-0.5 accent-[var(--accent)]"
      />
      <span className="flex flex-col">
        <span>{label}</span>
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          {hint}
        </span>
      </span>
    </label>
  );
}

function countLabel(n: number) {
  return n === 1 ? '1 work item' : `${n} work items`;
}
