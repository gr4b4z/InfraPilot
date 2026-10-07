import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  AlertTriangle,
  CheckCircle,
  ExternalLink,
  Loader2,
  Lock,
  Rocket,
  ShieldCheck,
  X,
  XCircle,
} from 'lucide-react';
import { api } from '@/lib/api';
import type { BulkApproveOutcome, BulkDecisionOutcome, PromotionCandidate } from '@/lib/api';
import { approvalDeploys } from '@/lib/approvalEffect';
import { Dialog } from '@/components/ui/Dialog';
import { useEnvColor } from '@/components/environments/useEnvColor';
import { useSettingsStore } from '@/stores/settingsStore';

/**
 * Mass approve: one gate, one environment, many promotions. A release night puts forty promotions in
 * front of the same release manager, all asking the same question. One at a time that is forty detail
 * pages. Here it is three choices — environment, gate, which rows — and one confirmation.
 *
 * The gate is chosen, not inferred. Somebody in two gates (the QA manager who is also the release
 * manager) has to say which signature they are giving, and saying it once for the batch is the
 * point. The server signs every requirement of that gate the caller is eligible for, per row, with
 * the same guards a single approval meets (see `ApproveGateAsync`).
 *
 * The checklist shows every promotion in the environment still short of that gate, not only the
 * caller's. Rows they cannot sign stay visible but disabled, with the reason. A release manager
 * counting forty promotions needs to see the four that will not go through, and why, before
 * confirming, not after. For an administrator those rows can be bypassed instead, and so can any row
 * they could approve. Bypass forces the promotion to Approved without its gate, so it takes a reason,
 * exactly as it does on the promotion page.
 *
 * Fetches its own Pending list, unfiltered: the page's filters narrow what the reader browses, and a
 * batch decision should not quietly inherit a product filter set an hour ago. The page's environment
 * and gate filters are used only as the starting choice.
 */

type RowAction = 'approve' | 'bypass';

interface Row {
  candidate: PromotionCandidate;
  /** The caller can sign the chosen gate here right now. */
  approvable: boolean;
  /** The other gates it is still waiting on, which this approval leaves open. */
  otherGates: string[];
}

type RowOutcome =
  | { kind: 'approved' | 'signed'; pendingGates: string[]; workItemsOutstanding: boolean }
  | { kind: 'bypassed' }
  | { kind: 'failed'; error: string };

const sameName = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'accent' }) === 0;

export function MassApproveDialog({
  isAdmin,
  initialTargetEnv,
  initialGate,
  onClose,
  onApplied,
}: {
  isAdmin: boolean;
  /** Starting environment — the page's target-env filter, when one is set. */
  initialTargetEnv?: string;
  /** Starting gate — the page's gate filter, when one is set. */
  initialGate?: string;
  onClose: () => void;
  /** Called once a batch has been sent, whatever came of it, so the page can re-read its lists. */
  onApplied: () => void;
}) {
  const getOrderedEnvironments = useSettingsStore((s) => s.getOrderedEnvironments);
  const getDisplayName = useSettingsStore((s) => s.getDisplayName);

  // null while loading. Refetched for "Approve another gate", so a second batch starts from what the
  // first one left.
  const [pending, setPending] = useState<PromotionCandidate[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadTick, setLoadTick] = useState(0);

  const [env, setEnv] = useState(initialTargetEnv ?? '');
  const [gate, setGate] = useState(initialGate ?? '');
  // The rows ticked, and as what. Kept per (env, gate): switching the gate starts from its own
  // defaults rather than carrying ticks over to rows that mean something else now.
  const [picked, setPicked] = useState<{ key: string; actions: Map<string, RowAction> } | null>(null);
  const [comment, setComment] = useState('');
  const [reason, setReason] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [outcomes, setOutcomes] = useState<Map<string, RowOutcome> | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .listPromotions({ status: 'Pending' })
      .then((data) => {
        if (!cancelled) setPending(data.candidates ?? []);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setPending([]);
        setLoadError(e instanceof Error ? e.message : 'Could not load the pending promotions.');
      });
    return () => {
      cancelled = true;
    };
  }, [loadTick]);

  // ── Environment ──────────────────────────────────────────────────────────

  const envCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const c of pending ?? []) counts.set(c.targetEnv, (counts.get(c.targetEnv) ?? 0) + 1);
    return counts;
  }, [pending]);
  // Deployment order (dev → staging → prod), the order the reader thinks in.
  const envOptions = useMemo(
    () => getOrderedEnvironments(Array.from(envCounts.keys())),
    [envCounts, getOrderedEnvironments],
  );
  // The chosen environment while it still has pending promotions; the only one when there is one.
  const selectedEnv =
    envOptions.find((e) => sameName(e, env)) ?? (envOptions.length === 1 ? envOptions[0] : '');

  // ── Gate ─────────────────────────────────────────────────────────────────

  const inEnv = useMemo(
    () => (pending ?? []).filter((c) => c.targetEnv === selectedEnv),
    [pending, selectedEnv],
  );
  const gateOptions = useMemo(() => {
    const byName = new Map<string, { name: string; waiting: number; yours: number }>();
    for (const c of inEnv) {
      for (const g of c.pendingGates ?? []) {
        const entry = byName.get(g) ?? { name: g, waiting: 0, yours: 0 };
        entry.waiting++;
        if ((c.approvableGates ?? []).includes(g)) entry.yours++;
        byName.set(g, entry);
      }
    }
    return Array.from(byName.values()).sort((a, b) => a.name.localeCompare(b.name));
  }, [inEnv]);
  const selectedGate =
    gateOptions.find((g) => sameName(g.name, gate))?.name ??
    (gateOptions.length === 1 ? gateOptions[0].name : '');

  // ── Checklist ────────────────────────────────────────────────────────────

  const rows = useMemo((): Row[] => {
    if (!selectedGate) return [];
    return inEnv
      .filter((c) => (c.pendingGates ?? []).includes(selectedGate))
      .map((c) => ({
        candidate: c,
        approvable: (c.approvableGates ?? []).includes(selectedGate),
        otherGates: (c.pendingGates ?? []).filter((g) => g !== selectedGate),
      }))
      // Yours first, so the rows you are about to sign sit together above the ones you cannot.
      .sort(
        (a, b) =>
          Number(b.approvable) - Number(a.approvable) ||
          a.candidate.product.localeCompare(b.candidate.product) ||
          a.candidate.service.localeCompare(b.candidate.service),
      );
  }, [inEnv, selectedGate]);

  const scopeKey = `${selectedEnv}\u0000${selectedGate}`;
  // Until the reader touches a row, every row they can approve is ticked. Nothing is pre-ticked for
  // bypass: an override is always somebody's deliberate choice.
  const defaultActions = useMemo(
    () => new Map<string, RowAction>(rows.filter((r) => r.approvable).map((r) => [r.candidate.id, 'approve'])),
    [rows],
  );
  const actions = picked?.key === scopeKey ? picked.actions : defaultActions;
  const updateActions = (change: (next: Map<string, RowAction>) => void) => {
    const next = new Map(actions);
    change(next);
    setPicked({ key: scopeKey, actions: next });
  };

  const approvableRows = rows.filter((r) => r.approvable);
  const otherRows = rows.filter((r) => !r.approvable);
  const allApprovableTicked = approvableRows.length > 0 && approvableRows.every((r) => actions.has(r.candidate.id));
  const allOthersBypassed = otherRows.length > 0 && otherRows.every((r) => actions.get(r.candidate.id) === 'bypass');

  const chosen = rows.filter((r) => actions.has(r.candidate.id));
  const approveRows = chosen.filter((r) => actions.get(r.candidate.id) === 'approve');
  const bypassRows = chosen.filter((r) => actions.get(r.candidate.id) === 'bypass');
  // Rows that can be live the moment this goes through, on an edge where approval is the last stop:
  // a bypass always finishes the gate, and an approval does when this was the last gate left. "Can",
  // because a gate wanting two people is not finished by one of them.
  const deployingCount = chosen.filter(
    (r) =>
      approvalDeploys(r.candidate) &&
      (actions.get(r.candidate.id) === 'bypass' || r.otherGates.length === 0),
  ).length;

  const reasonMissing = bypassRows.length > 0 && reason.trim().length === 0;
  const canSubmit = chosen.length > 0 && !reasonMissing && !submitting;

  const toggleRow = (row: Row) =>
    updateActions((next) => {
      const id = row.candidate.id;
      if (next.has(id)) next.delete(id);
      else if (row.approvable) next.set(id, 'approve');
      else if (isAdmin) next.set(id, 'bypass');
    });

  const toggleAllApprovable = () =>
    updateActions((next) => {
      for (const r of approvableRows) {
        if (allApprovableTicked) next.delete(r.candidate.id);
        else if (!next.has(r.candidate.id)) next.set(r.candidate.id, 'approve');
      }
    });

  const toggleBypassOthers = () =>
    updateActions((next) => {
      for (const r of otherRows) {
        if (allOthersBypassed) next.delete(r.candidate.id);
        else next.set(r.candidate.id, 'bypass');
      }
    });

  const submit = async () => {
    if (!canSubmit) return;
    setSubmitting(true);
    setSubmitError(null);
    const results = new Map<string, RowOutcome>();
    try {
      if (approveRows.length > 0) {
        const response = await api.bulkApprovePromotions(
          approveRows.map((r) => r.candidate.id),
          comment.trim() || undefined,
          selectedGate,
        );
        for (const r of response.results) results.set(r.id, approveOutcome(r));
      }
      if (bypassRows.length > 0) {
        const response = await api.bulkBypassPromotions(
          bypassRows.map((r) => r.candidate.id),
          reason.trim(),
        );
        for (const r of response.results) results.set(r.id, bypassOutcome(r));
      }
    } catch (e) {
      // The batch failed as a whole (network, server error). Some rows may have gone through before it
      // did; the rows we have no answer for say so rather than claiming either way.
      setSubmitError(
        (e instanceof Error ? e.message : 'The request failed.') +
          ' Some promotions may already have been approved — the list below shows what is known.',
      );
    } finally {
      for (const r of chosen) {
        if (!results.has(r.candidate.id)) {
          results.set(r.candidate.id, { kind: 'failed', error: 'No answer from the server for this one.' });
        }
      }
      setOutcomes(results);
      setSubmitting(false);
      onApplied();
    }
  };

  const startOver = () => {
    setOutcomes(null);
    setSubmitError(null);
    setPicked(null);
    setComment('');
    setReason('');
    setPending(null);
    setLoadError(null);
    setLoadTick((t) => t + 1);
  };

  const envLabel = selectedEnv ? getDisplayName(selectedEnv) : '';

  return (
    <Dialog onClose={submitting ? () => {} : onClose} ariaLabel="Mass approve" width={760}>
      <div
        className="flex items-center justify-between gap-3 px-4 py-3 border-b"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Mass approve
          </h2>
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            Sign one gate on every promotion waiting for it in an environment.
          </p>
        </div>
        <button
          type="button"
          onClick={onClose}
          disabled={submitting}
          aria-label="Close"
          className="rounded-lg p-1.5 transition-opacity hover:opacity-70 disabled:opacity-40"
          style={{ color: 'var(--text-muted)' }}
        >
          <X size={16} />
        </button>
      </div>

      {outcomes ? (
        <ResultsView
          rows={chosen}
          outcomes={outcomes}
          gate={selectedGate}
          envLabel={envLabel}
          error={submitError}
          onStartOver={startOver}
          onClose={onClose}
        />
      ) : (
        <>
          <div className="px-4 py-3 space-y-4">
            {pending === null ? (
              <div className="flex items-center gap-2 text-[13px]" style={{ color: 'var(--text-muted)' }}>
                <Loader2 size={14} className="animate-spin" />
                Loading pending promotions…
              </div>
            ) : loadError ? (
              <p className="text-[13px]" style={{ color: 'var(--danger)' }}>
                {loadError}
              </p>
            ) : envOptions.length === 0 ? (
              <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
                Nothing is waiting for approval right now.
              </p>
            ) : (
              <>
                <Section label="1. Environment">
                  {envOptions.map((e) => (
                    <EnvChoice
                      key={e}
                      env={e}
                      label={getDisplayName(e)}
                      count={envCounts.get(e) ?? 0}
                      active={e === selectedEnv}
                      onSelect={() => setEnv(e)}
                    />
                  ))}
                </Section>

                {selectedEnv && (
                  <Section label="2. Gate">
                    {gateOptions.length === 0 ? (
                      <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                        No promotion to {envLabel} is waiting on an approval gate.
                      </p>
                    ) : (
                      gateOptions.map((g) => {
                        const active = g.name === selectedGate;
                        return (
                          <button
                            key={g.name}
                            type="button"
                            onClick={() => setGate(g.name)}
                            aria-pressed={active}
                            className="flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
                            style={{
                              borderColor: active ? 'var(--accent)' : 'var(--border-color)',
                              backgroundColor: active ? 'var(--accent-bg)' : 'var(--bg-primary)',
                              color: active ? 'var(--accent)' : 'var(--text-secondary)',
                            }}
                          >
                            <ShieldCheck size={12} />
                            {g.name}
                            <span className="text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
                              {g.waiting} waiting · {g.yours} yours
                            </span>
                          </button>
                        );
                      })
                    )}
                  </Section>
                )}

                {selectedGate && (
                  <div>
                    <div className="flex flex-wrap items-center justify-between gap-2 mb-2">
                      <span
                        className="text-[11px] font-semibold uppercase tracking-wider"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        3. Promotions to {envLabel} waiting on {selectedGate} ({rows.length})
                      </span>
                      <div className="flex items-center gap-3 text-[12px]">
                        {approvableRows.length > 0 && (
                          <label className="flex items-center gap-1.5 cursor-pointer" style={{ color: 'var(--text-secondary)' }}>
                            <input
                              type="checkbox"
                              className="rounded"
                              checked={allApprovableTicked}
                              onChange={toggleAllApprovable}
                            />
                            All you can approve ({approvableRows.length})
                          </label>
                        )}
                        {isAdmin && otherRows.length > 0 && (
                          <label className="flex items-center gap-1.5 cursor-pointer" style={{ color: 'var(--warning)' }}>
                            <input
                              type="checkbox"
                              className="rounded"
                              checked={allOthersBypassed}
                              onChange={toggleBypassOthers}
                            />
                            Bypass the {otherRows.length} you can't approve
                          </label>
                        )}
                      </div>
                    </div>
                    <ul
                      className="rounded-lg border divide-y overflow-y-auto"
                      style={{ borderColor: 'var(--border-color)', maxHeight: '42vh' }}
                      aria-label={`Promotions waiting on ${selectedGate}`}
                    >
                      {rows.map((row) => (
                        <ChecklistRow
                          key={row.candidate.id}
                          row={row}
                          gate={selectedGate}
                          action={actions.get(row.candidate.id) ?? null}
                          isAdmin={isAdmin}
                          onToggle={() => toggleRow(row)}
                          onSetAction={(a) => updateActions((next) => next.set(row.candidate.id, a))}
                        />
                      ))}
                    </ul>
                  </div>
                )}

                {chosen.length > 0 && (
                  <div className="space-y-3">
                    {deployingCount > 0 && (
                      <p className="flex items-start gap-1.5 text-[12px]" style={{ color: 'var(--warning)' }}>
                        <AlertTriangle size={12} className="shrink-0 mt-0.5" />
                        <span>
                          {deployingCount === chosen.length
                            ? chosen.length === 1 ? 'This' : `All ${chosen.length}`
                            : `${deployingCount} of ${chosen.length}`}{' '}
                          can deploy to {envLabel} as soon as this goes through — nothing outside
                          InfraPortal has to say yes.
                        </span>
                      </p>
                    )}
                    {approveRows.length > 0 && (
                      <TextField
                        label="Comment (optional) — added to every approval"
                        value={comment}
                        onChange={setComment}
                        disabled={submitting}
                      />
                    )}
                    {bypassRows.length > 0 && (
                      <TextField
                        label="Reason for bypassing — recorded on every bypassed promotion"
                        required
                        value={reason}
                        onChange={setReason}
                        disabled={submitting}
                        placeholder="Why these go out without their gate…"
                      />
                    )}
                  </div>
                )}
              </>
            )}
          </div>

          <div
            className="px-4 py-3 border-t flex flex-wrap items-center justify-between gap-3"
            style={{ borderColor: 'var(--border-color)' }}
          >
            <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
              {chosen.length === 0
                ? 'Tick the promotions to include.'
                : summaryLine(approveRows.length, bypassRows.length, selectedGate)}
            </span>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={onClose}
                disabled={submitting}
                className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80 disabled:opacity-50"
                style={{ color: 'var(--text-secondary)', backgroundColor: 'var(--bg-secondary)' }}
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={submit}
                disabled={!canSubmit}
                title={reasonMissing ? 'Enter a reason for the bypass first' : undefined}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-[12px] font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-50"
                style={{
                  backgroundColor: bypassRows.length > 0 ? 'var(--warning-solid)' : 'var(--success-solid)',
                }}
              >
                {submitting && <Loader2 size={12} className="animate-spin" />}
                {confirmLabel(approveRows.length, bypassRows.length, selectedGate)}
              </button>
            </div>
          </div>
        </>
      )}
    </Dialog>
  );
}

function approveOutcome(r: BulkApproveOutcome): RowOutcome {
  if (!r.ok) return { kind: 'failed', error: r.error };
  return {
    kind: r.status === 'Pending' ? 'signed' : 'approved',
    pendingGates: r.pendingGates ?? [],
    workItemsOutstanding: r.workItemsOutstanding ?? false,
  };
}

function bypassOutcome(r: BulkDecisionOutcome): RowOutcome {
  return r.ok ? { kind: 'bypassed' } : { kind: 'failed', error: r.error };
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

function confirmLabel(approve: number, bypass: number, gate: string) {
  if (approve > 0 && bypass > 0) return `Approve ${approve} · bypass ${bypass}`;
  if (bypass > 0) return `Bypass ${plural(bypass, 'promotion')}`;
  if (approve > 0) return `Approve ${gate} on ${plural(approve, 'promotion')}`;
  return 'Approve';
}

function summaryLine(approve: number, bypass: number, gate: string) {
  const parts: string[] = [];
  if (approve > 0) parts.push(`${gate} signed on ${approve}`);
  if (bypass > 0) parts.push(`${bypass} bypassed without their gate`);
  return parts.join(' · ');
}

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div
        className="text-[11px] font-semibold uppercase tracking-wider mb-2"
        style={{ color: 'var(--text-muted)' }}
      >
        {label}
      </div>
      <div className="flex flex-wrap items-center gap-2">{children}</div>
    </div>
  );
}

/** An environment to pick, in its own colour so prod reads as prod. */
function EnvChoice({
  env,
  label,
  count,
  active,
  onSelect,
}: {
  env: string;
  label: string;
  count: number;
  active: boolean;
  onSelect: () => void;
}) {
  const { fg, bg, border, solid } = useEnvColor(env);
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={active}
      className="flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[13px] font-medium transition-colors"
      style={{
        borderColor: active ? border : 'var(--border-color)',
        backgroundColor: active ? bg : 'var(--bg-primary)',
        color: active ? fg : 'var(--text-secondary)',
      }}
    >
      <span aria-hidden className="inline-block h-2 w-2 rounded-full" style={{ backgroundColor: solid }} />
      {label}
      <span className="text-[11px] font-normal" style={{ color: 'var(--text-muted)' }}>
        {count} pending
      </span>
    </button>
  );
}

function ChecklistRow({
  row,
  gate,
  action,
  isAdmin,
  onToggle,
  onSetAction,
}: {
  row: Row;
  gate: string;
  action: RowAction | null;
  isAdmin: boolean;
  onToggle: () => void;
  onSetAction: (action: RowAction) => void;
}) {
  const c = row.candidate;
  const selectable = row.approvable || isAdmin;
  const from = c.fromVersion ?? c.targetCurrentVersion;
  const notYours = c.workItemsOutstanding
    ? {
        label: 'Waiting on work items',
        title: `Nobody may sign ${gate} until every work item is signed off.`,
        icon: Lock,
      }
    : {
        label: 'Not yours to approve',
        title: `You are not an approver for ${gate} here, or you have already signed it.`,
        icon: XCircle,
      };

  return (
    <li
      className="flex items-center gap-3 px-3 py-2"
      style={{
        backgroundColor: action ? 'var(--bg-secondary)' : 'var(--bg-primary)',
        opacity: selectable ? 1 : 0.65,
      }}
    >
      <label className={`flex min-w-0 flex-1 items-start gap-3 ${selectable ? 'cursor-pointer' : ''}`}>
        <input
          type="checkbox"
          className="rounded mt-0.5 shrink-0"
          checked={action !== null}
          disabled={!selectable}
          onChange={onToggle}
          aria-label={`${c.product} / ${c.service} ${c.version}`}
        />
        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-baseline gap-x-2">
            <span className="text-[13px] font-medium truncate" style={{ color: 'var(--text-primary)' }}>
              {c.product} / {c.service}
            </span>
            <span className="font-mono text-[11px]" style={{ color: 'var(--text-secondary)' }}>
              {from ? `${from} → ` : ''}
              {c.version}
            </span>
          </span>
          <span className="flex flex-wrap items-center gap-x-3 text-[11px]" style={{ color: 'var(--text-muted)' }}>
            {row.otherGates.length > 0 ? (
              <span>then {row.otherGates.join(', ')}</span>
            ) : (
              <span className="inline-flex items-center gap-1">
                {approvalDeploys(c) && <Rocket size={10} />}
                last gate{approvalDeploys(c) ? ' · deploys on approval' : ''}
              </span>
            )}
            {!row.approvable && (
              <span className="inline-flex items-center gap-1" title={notYours.title}>
                <notYours.icon size={10} />
                {notYours.label}
              </span>
            )}
          </span>
        </span>
      </label>

      {isAdmin && action !== null && (
        <div
          className="flex shrink-0 overflow-hidden rounded-md border text-[11px] font-medium"
          style={{ borderColor: 'var(--border-color)' }}
          role="group"
          aria-label="Action"
        >
          <ActionToggle
            label="Approve"
            active={action === 'approve'}
            disabled={!row.approvable}
            title={row.approvable ? `Sign ${gate}` : notYours.title}
            tone="var(--success)"
            onClick={() => onSetAction('approve')}
          />
          <ActionToggle
            label="Bypass"
            active={action === 'bypass'}
            title="Force to Approved without its gate (admin override)"
            tone="var(--warning)"
            onClick={() => onSetAction('bypass')}
          />
        </div>
      )}

      <Link
        to={`/promotions/${c.id}`}
        target="_blank"
        rel="noopener noreferrer"
        className="shrink-0 transition-opacity hover:opacity-70"
        style={{ color: 'var(--text-muted)' }}
        title="Open this promotion in a new tab"
        aria-label={`Open ${c.product} / ${c.service} in a new tab`}
      >
        <ExternalLink size={12} />
      </Link>
    </li>
  );
}

function ActionToggle({
  label,
  active,
  disabled = false,
  title,
  tone,
  onClick,
}: {
  label: string;
  active: boolean;
  disabled?: boolean;
  title: string;
  tone: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-pressed={active}
      title={title}
      className="px-2 py-0.5 transition-colors disabled:cursor-not-allowed disabled:opacity-40"
      style={{
        backgroundColor: active ? tone : 'var(--bg-primary)',
        color: active ? '#fff' : 'var(--text-secondary)',
      }}
    >
      {label}
    </button>
  );
}

function TextField({
  label,
  value,
  onChange,
  required = false,
  disabled = false,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  required?: boolean;
  disabled?: boolean;
  placeholder?: string;
}) {
  return (
    <label className="block">
      <span className="block text-[12px] mb-1" style={{ color: 'var(--text-muted)' }}>
        {label}
        {required && <span style={{ color: 'var(--danger)' }}> *</span>}
      </span>
      <textarea
        value={value}
        onChange={(e) => onChange(e.target.value)}
        rows={2}
        disabled={disabled}
        placeholder={placeholder}
        className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none outline-none focus:border-[var(--accent)]"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-secondary)',
          color: 'var(--text-primary)',
        }}
      />
    </label>
  );
}

/** After the batch: what happened to each row, failures first, so nothing that didn't go through is missed. */
function ResultsView({
  rows,
  outcomes,
  gate,
  envLabel,
  error,
  onStartOver,
  onClose,
}: {
  rows: Row[];
  outcomes: Map<string, RowOutcome>;
  gate: string;
  envLabel: string;
  error: string | null;
  onStartOver: () => void;
  onClose: () => void;
}) {
  const counts = { approved: 0, signed: 0, bypassed: 0, failed: 0 };
  for (const r of rows) {
    const o = outcomes.get(r.candidate.id);
    if (o) counts[o.kind]++;
  }
  const ordered = [...rows].sort(
    (a, b) =>
      Number(outcomes.get(b.candidate.id)?.kind === 'failed') -
      Number(outcomes.get(a.candidate.id)?.kind === 'failed'),
  );
  const headline = [
    counts.approved > 0 && `${counts.approved} approved`,
    counts.signed > 0 && `${counts.signed} signed, still waiting`,
    counts.bypassed > 0 && `${counts.bypassed} bypassed`,
    counts.failed > 0 && `${counts.failed} not done`,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <>
      <div className="px-4 py-3 space-y-3">
        <p className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
          {gate} on {envLabel}: {headline}
        </p>
        {error && (
          <p className="text-[12px]" style={{ color: 'var(--danger)' }}>
            {error}
          </p>
        )}
        <ul
          className="rounded-lg border divide-y overflow-y-auto"
          style={{ borderColor: 'var(--border-color)', maxHeight: '50vh' }}
          aria-label="Results"
        >
          {ordered.map((r) => {
            const outcome = outcomes.get(r.candidate.id);
            if (!outcome) return null;
            const view = outcomeView(outcome);
            return (
              <li key={r.candidate.id} className="flex items-start gap-3 px-3 py-2">
                <view.icon size={14} className="shrink-0 mt-0.5" style={{ color: view.color }} />
                <span className="min-w-0 flex-1">
                  <Link
                    to={`/promotions/${r.candidate.id}`}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-[13px] font-medium hover:underline"
                    style={{ color: 'var(--text-primary)' }}
                  >
                    {r.candidate.product} / {r.candidate.service}
                  </Link>{' '}
                  <span className="font-mono text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    {r.candidate.version}
                  </span>
                  <span className="block text-[12px]" style={{ color: view.color }}>
                    {view.text}
                  </span>
                </span>
              </li>
            );
          })}
        </ul>
      </div>
      <div
        className="px-4 py-3 border-t flex items-center justify-end gap-2"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <button
          type="button"
          onClick={onStartOver}
          className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-secondary)', backgroundColor: 'var(--bg-secondary)' }}
        >
          Approve another gate
        </button>
        <button
          type="button"
          onClick={onClose}
          className="px-3 py-1.5 rounded-lg text-[12px] font-semibold text-white transition-opacity hover:opacity-90"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          Done
        </button>
      </div>
    </>
  );
}

function outcomeView(outcome: RowOutcome): { icon: typeof CheckCircle; color: string; text: string } {
  switch (outcome.kind) {
    case 'approved':
      return { icon: CheckCircle, color: 'var(--success)', text: 'Approved — that was its last gate.' };
    case 'signed':
      return {
        icon: CheckCircle,
        color: 'var(--success)',
        text: outcome.workItemsOutstanding
          ? 'Signed — still waiting on its work items.'
          : outcome.pendingGates.length > 0
            ? `Signed — still waiting on ${outcome.pendingGates.join(', ')}.`
            : 'Signed — still waiting on another approver.',
      };
    case 'bypassed':
      return { icon: Rocket, color: 'var(--warning)', text: 'Bypassed — approved without its gate.' };
    case 'failed':
      return { icon: XCircle, color: 'var(--danger)', text: outcome.error };
  }
}
