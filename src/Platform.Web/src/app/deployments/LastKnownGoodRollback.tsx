import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { AlertTriangle, ArrowRight, CheckCircle2, Loader2, Undo2, X } from 'lucide-react';
import { api } from '@/lib/api';
import type { LastKnownGoodOffer, RollbackRequest } from '@/lib/api';
import { Dialog } from '@/components/ui/Dialog';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';

/**
 * "Roll back to the last known good version" on a failed deployment.
 *
 * A failed deploy is the moment a rollback is wanted most and was hardest to raise: the generic form
 * starts from the last *succeeded* deploy, so the version to go back to read as "already running"
 * and was refused. The server now treats a failure that is still the environment's latest word on
 * the service as something to undo, and this is the shortcut to it. The target is picked by the
 * server (`GET /rollbacks/last-known-good/{eventId}`), never typed here, so the button and the
 * request it raises cannot disagree about which version is "last known good".
 *
 * The confirmation says what the click sets in motion before it happens: who has to approve, which
 * promotion its approval reopens, and, when nothing subscribes to `rollback.approved`, that approving
 * it will not deploy anything by itself.
 *
 * Used in two places: the failure callout of a failed deployment, and the failed-deploy warning of
 * the promotion that shipped it. `hideUnavailable` keeps the second one quiet once there is nothing
 * to roll back — there the reason is about a deployment, not the promotion being read.
 */
export function LastKnownGoodRollback({ eventId, hideUnavailable = false }: {
  eventId: string;
  hideUnavailable?: boolean;
}) {
  const rollbacksEnabled = useFeatureFlagsStore((s) => s.isEnabled(FeatureFlag.Rollbacks));
  const [offer, setOffer] = useState<LastKnownGoodOffer | null>(null);
  const [open, setOpen] = useState(false);

  // An open rollback for this service moves through Pending → Approved → RolledBack while the page
  // is up; the notice follows it.
  const tick = useEntityRefresh(['rollback', 'deployment']);
  const load = useCallback(() => {
    if (!rollbacksEnabled) return;
    api
      .getLastKnownGoodRollback(eventId)
      .then(setOffer)
      // No offer is not an error on a page about a failure: the callout simply has no action.
      .catch(() => setOffer(null));
  }, [eventId, rollbacksEnabled]);

  useEffect(() => {
    load();
  }, [load, tick]);

  if (!rollbacksEnabled || !offer || offer.eventId !== eventId) return null;

  if (!offer.available) {
    return offer.reason && !hideUnavailable ? (
      <p className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
        <Undo2 size={11} className="inline mr-1 -mt-0.5" />
        {offer.reason}
      </p>
    ) : null;
  }

  if (offer.openRollback) {
    return (
      <div className="flex items-center gap-2 flex-wrap text-[12.5px]" style={{ color: 'var(--text-primary)' }}>
        <Undo2 size={13} style={{ color: 'var(--warning)' }} />
        <span>
          A rollback to <span className="font-mono">v{offer.lastGoodVersion}</span> is{' '}
          <strong>{statusLabel(offer.openRollback.status)}</strong>.
        </span>
        <Link to={rollbacksPath(offer)} className="font-medium hover:underline" style={{ color: 'var(--accent)' }}>
          Open rollbacks
        </Link>
      </div>
    );
  }

  return (
    <>
      <div className="flex items-center gap-2 flex-wrap">
        <button
          type="button"
          onClick={() => setOpen(true)}
          disabled={!offer.canCreate}
          data-guide-anchor="deployment-last-known-good-rollback"
          // Wraps on a phone: the label plus a full build version is wider than the screen.
          className="inline-flex flex-wrap items-center gap-x-1.5 gap-y-0.5 px-3 py-1.5 rounded-lg text-[13px] font-medium text-left transition-opacity hover:opacity-90 disabled:opacity-50"
          style={{ backgroundColor: 'var(--danger-solid)', color: '#fff' }}
          title={offer.canCreate ? undefined : (offer.createBlockedReason ?? undefined)}
        >
          <Undo2 size={13} />
          Roll back to last known good version
          <span className="font-mono">v{offer.lastGoodVersion}</span>
        </button>
        {!offer.canCreate && offer.createBlockedReason && (
          <span className="text-[12px]" style={{ color: 'var(--text-secondary)' }}>
            {offer.createBlockedReason}
          </span>
        )}
      </div>
      {open && (
        <ConfirmLastKnownGood
          offer={offer}
          onClose={() => {
            setOpen(false);
            load();
          }}
        />
      )}
    </>
  );
}

function ConfirmLastKnownGood({ offer, onClose }: { offer: LastKnownGoodOffer; onClose: () => void }) {
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [created, setCreated] = useState<RollbackRequest | null>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);

  const submit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      setCreated(await api.createLastKnownGoodRollback(offer.eventId, reason.trim() || undefined));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create the rollback');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Dialog onClose={submitting ? () => {} : onClose} ariaLabel="Roll back to the last known good version" initialFocusRef={confirmRef}>
      <div className="p-5 space-y-4">
        <div className="flex items-start gap-2">
          <Undo2 size={18} className="mt-0.5" style={{ color: 'var(--danger)' }} />
          <div className="min-w-0">
            <h2 className="text-[15px] font-semibold" style={{ color: 'var(--text-primary)' }}>
              Roll back to the last known good version
            </h2>
            <div className="flex items-center gap-2 mt-1 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
              <span className="font-medium">{offer.service}</span>
              <EnvBadge env={offer.targetEnv} />
              <span style={{ color: 'var(--text-muted)' }}>{offer.product}</span>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={submitting}
            aria-label="Close"
            className="ml-auto p-1 rounded transition-opacity hover:opacity-80 disabled:opacity-40"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={16} />
          </button>
        </div>

        {/* From the failed version to the one that last worked — the whole decision in one line. */}
        <div
          className="flex items-stretch gap-2 rounded-lg border p-3 text-[12.5px]"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          <VersionCell
            label="Failed"
            version={offer.failedVersion}
            at={offer.failedAt}
            color="var(--danger)"
            eventId={offer.failedEventId}
          />
          <ArrowRight size={16} className="self-center shrink-0" style={{ color: 'var(--text-muted)' }} />
          <VersionCell
            label="Last deployed successfully"
            version={offer.lastGoodVersion}
            at={offer.lastGoodAt}
            color="var(--success)"
            eventId={offer.lastGoodEventId}
          />
        </div>

        {created ? (
          <Outcome offer={offer} created={created} />
        ) : (
          <>
            <ul className="space-y-2 text-[12.5px]" style={{ color: 'var(--text-secondary)' }}>
              <li>{approvalSentence(offer)}</li>
              {offer.promotionsToReopen.map((p) => (
                <li key={p.id}>
                  Approving it reopens the{' '}
                  <Link to={`/promotions/${p.id}`} className="font-medium hover:underline" style={{ color: 'var(--accent)' }}>
                    {p.sourceEnv} → {offer.targetEnv} promotion of v{p.version}
                  </Link>{' '}
                  ({p.status.toLowerCase()}): its approval is cleared, and it needs a new one before that version
                  deploys again.
                </li>
              ))}
            </ul>

            {!offer.executorConfigured && (
              <div
                className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
                style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
              >
                <AlertTriangle size={14} className="shrink-0 mt-0.5" />
                <span>
                  No webhook delivers <code>rollback.approved</code> for {offer.product} in {offer.targetEnv}, so
                  approving this rollback will not deploy anything by itself. An admin adds the release
                  pipeline&apos;s subscription under Settings → Webhooks.
                </span>
              </div>
            )}

            <div className="space-y-1.5">
              <label htmlFor="lkg-reason" className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                Reason
              </label>
              <textarea
                id="lkg-reason"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                rows={2}
                maxLength={500}
                placeholder={`The deploy of ${offer.failedVersion} failed`}
                className="w-full rounded-lg border px-3 py-2 text-[13px] resize-none"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
              />
              <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                Optional. Shown to approvers and on the request; a ticket or incident key is enough.
              </p>
            </div>

            {error && (
              <div
                className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
                style={{ backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}
              >
                <AlertTriangle size={14} className="shrink-0 mt-0.5" />
                <span>{error}</span>
              </div>
            )}
          </>
        )}

        <div className="flex items-center justify-end gap-2 pt-1">
          {created ? (
            <>
              <Link
                to={rollbacksPath(offer)}
                className="px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
                style={{ border: '1px solid var(--border-color)', color: 'var(--text-primary)' }}
              >
                Open rollbacks
              </Link>
              <button
                type="button"
                onClick={onClose}
                className="px-3 py-2 rounded-lg text-[13px] font-medium"
                style={{ backgroundColor: 'var(--accent)', color: 'var(--accent-fg)' }}
              >
                Done
              </button>
            </>
          ) : (
            <>
              <button
                type="button"
                onClick={onClose}
                disabled={submitting}
                className="px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity hover:opacity-80"
                style={{ border: '1px solid var(--border-color)', color: 'var(--text-primary)' }}
              >
                Cancel
              </button>
              <button
                ref={confirmRef}
                type="button"
                onClick={submit}
                disabled={submitting}
                className="inline-flex items-center gap-1.5 px-3 py-2 rounded-lg text-[13px] font-medium transition-opacity disabled:opacity-60"
                style={{ backgroundColor: 'var(--danger-solid)', color: '#fff' }}
              >
                {submitting ? <Loader2 size={13} className="animate-spin" /> : <Undo2 size={13} />}
                Roll back to v{offer.lastGoodVersion}
              </button>
            </>
          )}
        </div>
      </div>
    </Dialog>
  );
}

function VersionCell({ label, version, at, color, eventId }: {
  label: string;
  version: string | null;
  at: string | null;
  color: string;
  eventId: string | null;
}) {
  return (
    <div className="min-w-0 flex-1">
      <div className="text-[11px] font-semibold uppercase tracking-wider" style={{ color }}>
        {label}
      </div>
      {eventId ? (
        <Link to={`/deployments/events/${eventId}`} className="font-mono font-medium hover:underline break-all" style={{ color: 'var(--text-primary)' }}>
          v{version}
        </Link>
      ) : (
        <span className="font-mono font-medium break-all" style={{ color: 'var(--text-primary)' }}>v{version}</span>
      )}
      {at && (
        <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          {formatDistanceToNow(new Date(at), { addSuffix: true })}
        </div>
      )}
    </div>
  );
}

function Outcome({ offer, created }: { offer: LastKnownGoodOffer; created: RollbackRequest }) {
  const approved = created.status === 'Approved' || created.status === 'RollingBack';
  return (
    <div
      className="flex items-start gap-2 px-3 py-2.5 rounded-lg text-[12.5px]"
      style={{ backgroundColor: 'var(--success-bg)', color: 'var(--text-primary)' }}
    >
      <CheckCircle2 size={15} className="shrink-0 mt-0.5" style={{ color: 'var(--success)' }} />
      <span>
        {approved
          ? `Rollback approved. ${offer.executorConfigured
              ? `The release pipeline now deploys v${offer.lastGoodVersion}; the request closes when that deploy lands.`
              : 'Nothing is subscribed to carry it out yet, so it waits until a release pipeline picks it up.'}`
          : offer.approval === 'unconfigured'
            ? 'Rollback raised. No rollback policy covers this environment, so an admin has to override its approval on the Rollbacks page.'
            : `Rollback raised and waiting for approval${offer.approvers.length ? ` by ${offer.approvers.join(', ')}` : ''}.`}
        {offer.promotionsToReopen.length > 0 && approved && ' The promotion of the failed version is back to pending.'}
      </span>
    </div>
  );
}

function approvalSentence(offer: LastKnownGoodOffer): string {
  switch (offer.approval) {
    case 'auto':
      return `The rollback policy for ${offer.product} in ${offer.targetEnv} approves it at once.`;
    case 'required':
      return `It waits for approval${offer.approvers.length ? ` by ${offer.approvers.join(', ')}` : ''} before anything deploys.`;
    default:
      return `No rollback policy covers ${offer.product} in ${offer.targetEnv}, so it waits for an admin to override its approval.`;
  }
}

function statusLabel(status: string): string {
  return status === 'RollingBack' ? 'rolling back' : status.toLowerCase();
}

function rollbacksPath(offer: LastKnownGoodOffer): string {
  return `/rollbacks?product=${encodeURIComponent(offer.product)}&targetEnv=${encodeURIComponent(offer.targetEnv)}`;
}
