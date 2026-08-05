import { useState } from 'react';
import { Trash2, Check, ArrowRight, RotateCcw } from 'lucide-react';
import { api } from '@/lib/api';
import type { PromotionReconcileResult } from '@/lib/api';
import { EnvBadge } from '@/components/environments/EnvBadge';

/**
 * Settings → Maintenance: data-repair actions for the messes that accumulate in a live install.
 * One card per task, and every task follows the same contract: a read-only preview first, then an
 * explicit apply — an admin should always be approving a reviewed count, never firing blind.
 */
export function MaintenanceSettings() {
  return (
    <div className="space-y-4">
      <DuplicateScanCard
        title="Duplicate deployment events"
        description="Duplicate deployment events can accumulate when CI systems retry ingest
          webhooks. Scan to see how many exist, then remove them. Duplicates are rows matching on
          product, service, environment, version, deployedAt and source — the earliest ingested row
          per group is kept."
        noun="duplicate"
        scan={() => api.getDeploymentDuplicatesPreview()}
        remove={() => api.removeDeploymentDuplicates()}
      />
      <LogRetentionCard />
      <PromotionReconcileCard />
      <DuplicateScanCard
        title="Duplicate promotion candidates"
        description="An earlier create path minted a new promotion per external request instead of
          reusing the existing one, leaving groups of identical copies. Scan to see how many exist,
          then remove them — the earliest copy per group is kept, along with its comment thread.
          Legitimate repeat history (a version deployed, rolled back and promoted again; a promotion
          superseded, retried and superseded again) is recognised and left alone."
        noun="duplicate"
        scan={() => api.getPromotionDuplicatesPreview()}
        remove={() => api.removePromotionDuplicates()}
      />
      <WebhookDeliveriesCard />
    </div>
  );
}

// ── Generic scan → remove card ──────────────────────────────────────────────────────────────

/**
 * The shared shape of a duplicates cleanup: one read-only scan reporting `{ groups, rows }`, one
 * destructive remove returning the same counts. Used by both the deploy-event and the
 * promotion-candidate dedup — the rules differ server-side, the ceremony doesn't.
 */
function DuplicateScanCard({
  title,
  description,
  noun,
  scan,
  remove,
}: {
  title: string;
  description: string;
  noun: string;
  scan: () => Promise<{ groups: number; rows: number }>;
  remove: () => Promise<{ groups: number; rows: number }>;
}) {
  const [scanResult, setScanResult] = useState<{ groups: number; rows: number } | null>(null);
  const [scanning, setScanning] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [removedResult, setRemovedResult] = useState<{ groups: number; rows: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleScan = async () => {
    setScanning(true);
    setError(null);
    setRemovedResult(null);
    try {
      setScanResult(await scan());
    } catch (e) {
      setError(e instanceof Error ? e.message : `Failed to scan for ${noun}s`);
    } finally {
      setScanning(false);
    }
  };

  const handleRemove = async () => {
    setRemoving(true);
    setError(null);
    try {
      setRemovedResult(await remove());
      setScanResult(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : `Failed to remove ${noun}s`);
    } finally {
      setRemoving(false);
    }
  };

  const handleCancel = () => {
    setScanResult(null);
    setError(null);
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          {title}
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          {description}
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        {!scanResult && (
          <button
            onClick={handleScan}
            disabled={scanning}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {scanning ? 'Scanning…' : `Scan for ${noun}s`}
          </button>
        )}

        {scanResult && scanResult.rows === 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              No {noun}s found.
            </span>
            <button
              onClick={handleCancel}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Dismiss
            </button>
          </>
        )}

        {scanResult && scanResult.rows > 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
              Found <strong>{scanResult.rows}</strong>{' '}
              {scanResult.rows === 1 ? noun : `${noun}s`} across{' '}
              <strong>{scanResult.groups}</strong>{' '}
              {scanResult.groups === 1 ? 'group' : 'groups'}.
            </span>
            <button
              onClick={handleRemove}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--danger, #dc2626)' }}
            >
              <Trash2 size={14} />
              {removing ? 'Removing…' : `Remove ${noun}s`}
            </button>
            <button
              onClick={handleCancel}
              disabled={removing}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {removedResult && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} />
            Removed {removedResult.rows}{' '}
            {removedResult.rows === 1 ? noun : `${noun}s`} across{' '}
            {removedResult.groups} {removedResult.groups === 1 ? 'group' : 'groups'}.
          </span>
        )}
      </div>

      {error && <CardError message={error} />}
    </section>
  );
}

// ── Deployment log retention ────────────────────────────────────────────────────────────────

function LogRetentionCard() {
  const [days, setDays] = useState(90);
  const [preview, setPreview] = useState<{ logs: number; bytes: number } | null>(null);
  const [removed, setRemoved] = useState<{ logs: number; bytes: number } | null>(null);
  const [running, setRunning] = useState<'preview' | 'apply' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handlePreview = async () => {
    setRunning('preview');
    setError(null);
    setRemoved(null);
    try {
      setPreview(await api.getDeploymentLogRetentionPreview(days));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to check logs');
    } finally {
      setRunning(null);
    }
  };

  const handleApply = async () => {
    setRunning('apply');
    setError(null);
    try {
      setRemoved(await api.removeOldDeploymentLogs(days));
      setPreview(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to purge logs');
    } finally {
      setRunning(null);
    }
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Deployment log retention
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Captured pipeline output (Helm printouts, failure diagnostics) is the largest thing stored
          per deployment, and it matters while somebody is debugging that deploy — not months later.
          This deletes the stored log blocks of deployments older than the cutoff; the deployments
          themselves, and everything else on them, stay.
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        <label
          className="inline-flex items-center gap-1.5 text-[13px]"
          style={{ color: 'var(--text-muted)' }}
        >
          Older than
          <input
            type="number"
            min={1}
            max={3650}
            value={days}
            onChange={(e) => {
              setDays(Math.max(1, Math.min(3650, Number(e.target.value) || 1)));
              // A new cutoff invalidates the previewed counts — force a fresh preview before apply.
              setPreview(null);
            }}
            className="w-20 rounded-lg border px-2 py-1.5 text-[13px]"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-primary)',
              color: 'var(--text-primary)',
            }}
          />
          days
        </label>

        {!preview && (
          <button
            onClick={handlePreview}
            disabled={running !== null}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {running === 'preview' ? 'Checking…' : 'Check size'}
          </button>
        )}

        {preview && preview.logs === 0 && (
          <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            No stored logs older than {days} days.
          </span>
        )}

        {preview && preview.logs > 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
              <strong>{preview.logs}</strong> log {preview.logs === 1 ? 'block' : 'blocks'},{' '}
              <strong>{formatBytes(preview.bytes)}</strong>, on deployments older than {days} days.
            </span>
            <button
              onClick={handleApply}
              disabled={running !== null}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--danger, #dc2626)' }}
            >
              <Trash2 size={14} />
              {running === 'apply' ? 'Purging…' : 'Purge logs'}
            </button>
            <button
              onClick={() => setPreview(null)}
              disabled={running !== null}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {removed && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} />
            Purged {removed.logs} log {removed.logs === 1 ? 'block' : 'blocks'} (
            {formatBytes(removed.bytes)}).
          </span>
        )}
      </div>

      {error && <CardError message={error} />}
    </section>
  );
}

// ── Stranded promotions ─────────────────────────────────────────────────────────────────────

/**
 * Runs the reconcile pass over open promotions (see the admin reconcile-completions endpoint).
 * Preview is a dry run of the exact same assessment, so the list shown is the list applied —
 * modulo promotions that settle on their own between the two clicks, which the apply-side
 * summary makes visible by reporting its own counts.
 */
function PromotionReconcileCard() {
  const [preview, setPreview] = useState<PromotionReconcileResult | null>(null);
  const [applied, setApplied] = useState<PromotionReconcileResult | null>(null);
  const [running, setRunning] = useState<'preview' | 'apply' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async (dryRun: boolean) => {
    setRunning(dryRun ? 'preview' : 'apply');
    setError(null);
    try {
      const result = await api.reconcilePromotionCompletions(dryRun);
      if (dryRun) {
        setApplied(null);
        setPreview(result);
      } else {
        setPreview(null);
        setApplied(result);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Reconcile failed');
    } finally {
      setRunning(null);
    }
  };

  const handleCancel = () => {
    setPreview(null);
    setError(null);
  };

  const summary = (r: PromotionReconcileResult, tense: 'would' | 'did') => (
    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
      Examined <strong>{r.examined}</strong> open{' '}
      {r.examined === 1 ? 'promotion' : 'promotions'}:{' '}
      <strong>{r.closed}</strong> {tense === 'would' ? 'would close' : 'closed'} as deployed,{' '}
      <strong>{r.superseded}</strong> {tense === 'would' ? 'would be' : ''} superseded as overtaken,{' '}
      <strong>{r.leftOpen}</strong> left open.
    </span>
  );

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Stranded promotions
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          A promotion created after its version had already been deployed — or whose target
          environment has since moved to a newer version — can sit in “awaiting deploy” forever.
          This settles open promotions against real deploy history: ones whose version shipped are
          closed as Deployed (dated from the deploy itself), ones a newer version overtook are
          superseded, and anything ambiguous — a rolled-back target, a failed-only deploy, a version
          that can’t be ordered — is left alone.
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        {!preview && (
          <button
            onClick={() => void run(true)}
            disabled={running !== null}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {running === 'preview' ? 'Checking…' : 'Preview changes'}
          </button>
        )}

        {preview && preview.candidates.length === 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              Nothing to settle — checked {preview.examined} open{' '}
              {preview.examined === 1 ? 'promotion' : 'promotions'}, deploy history has nothing to
              say about {preview.examined === 1 ? 'it' : 'them'}.
            </span>
            <button
              onClick={handleCancel}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Dismiss
            </button>
          </>
        )}

        {preview && preview.candidates.length > 0 && (
          <>
            {summary(preview, 'would')}
            <button
              onClick={() => void run(false)}
              disabled={running !== null}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--danger, #dc2626)' }}
            >
              {running === 'apply' ? 'Applying…' : `Apply (${preview.candidates.length})`}
            </button>
            <button
              onClick={handleCancel}
              disabled={running !== null}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {applied && (
          <span className="inline-flex items-center gap-1.5 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} />
            {summary(applied, 'did')}
          </span>
        )}
      </div>

      {/* What the pass would touch (or touched) — the reviewed list is the whole point of the
          preview step, so it renders in full rather than as a bare count. */}
      {(preview ?? applied) && (preview ?? applied)!.candidates.length > 0 && (
        <ReconcileTable result={(preview ?? applied)!} />
      )}

      {error && <CardError message={error} />}
    </section>
  );
}

function ReconcileTable({ result }: { result: PromotionReconcileResult }) {
  return (
    <div
      className="overflow-x-auto rounded-lg border"
      style={{ borderColor: 'var(--border-color)' }}
    >
      <table className="w-full text-[12px]">
        <thead>
          <tr
            className="text-left text-[11px] uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            <th className="px-3 py-2 font-semibold">Promotion</th>
            <th className="px-3 py-2 font-semibold">Edge</th>
            <th className="px-3 py-2 font-semibold">Version</th>
            <th className="px-3 py-2 font-semibold">Outcome</th>
          </tr>
        </thead>
        <tbody>
          {result.candidates.map((c) => (
            <tr
              key={c.id}
              className="border-t"
              style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
            >
              <td className="px-3 py-2 whitespace-nowrap">
                <span className="font-medium" style={{ color: 'var(--text-primary)' }}>
                  {c.product} / {c.service}
                </span>
              </td>
              <td className="px-3 py-2 whitespace-nowrap">
                <span className="inline-flex items-center gap-1">
                  <EnvBadge env={c.sourceEnv} />
                  <ArrowRight size={10} style={{ color: 'var(--text-muted)' }} />
                  <EnvBadge env={c.targetEnv} />
                </span>
              </td>
              <td className="px-3 py-2 font-mono whitespace-nowrap">{c.version}</td>
              <td className="px-3 py-2 whitespace-nowrap">
                {c.action === 'closed' ? (
                  <span style={{ color: 'var(--success)' }}>
                    Deployed — shipped {new Date(c.at).toLocaleDateString()}
                  </span>
                ) : (
                  <span style={{ color: 'var(--text-muted)' }}>
                    Superseded — target moved to <span className="font-mono">{c.landedVersion}</span>
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── Webhook deliveries ──────────────────────────────────────────────────────────────────────

const PURGE_CUTOFF_DAYS = 30;

function WebhookDeliveriesCard() {
  const [stats, setStats] = useState<{ failed: number; purgeable: number; oldestFailedAt: string | null } | null>(null);
  const [running, setRunning] = useState<'check' | 'retry' | 'purge' | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const handleCheck = async () => {
    setRunning('check');
    setError(null);
    setDone(null);
    try {
      setStats(await api.getWebhookDeliveryMaintenanceStats(PURGE_CUTOFF_DAYS));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to check deliveries');
    } finally {
      setRunning(null);
    }
  };

  const handleRetry = async () => {
    setRunning('retry');
    setError(null);
    try {
      const { retried } = await api.retryFailedWebhookDeliveries();
      setDone(`Re-queued ${retried} failed ${retried === 1 ? 'delivery' : 'deliveries'}.`);
      setStats(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Retry failed');
    } finally {
      setRunning(null);
    }
  };

  const handlePurge = async () => {
    setRunning('purge');
    setError(null);
    try {
      const { removed } = await api.purgeWebhookDeliveries(PURGE_CUTOFF_DAYS);
      setDone(`Purged ${removed} old ${removed === 1 ? 'delivery' : 'deliveries'}.`);
      setStats(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Purge failed');
    } finally {
      setRunning(null);
    }
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Webhook deliveries
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          After a receiver outage, deliveries that exhausted their retries sit as failed and nothing
          re-queues them — the per-delivery retry button on the webhook page covers one flaky call,
          not hundreds. Retry all failed re-queues them with their original payloads. Purge deletes
          delivered and failed records older than {PURGE_CUTOFF_DAYS} days; pending deliveries are
          never touched, whatever their age.
        </p>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        {!stats && (
          <button
            onClick={handleCheck}
            disabled={running !== null}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {running === 'check' ? 'Checking…' : 'Check deliveries'}
          </button>
        )}

        {stats && stats.failed === 0 && stats.purgeable === 0 && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
              Nothing to do — no failed deliveries, nothing older than {PURGE_CUTOFF_DAYS} days.
            </span>
            <button
              onClick={() => setStats(null)}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Dismiss
            </button>
          </>
        )}

        {stats && (stats.failed > 0 || stats.purgeable > 0) && (
          <>
            <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
              <strong>{stats.failed}</strong> failed
              {stats.oldestFailedAt && (
                <span style={{ color: 'var(--text-muted)' }}>
                  {' '}
                  (oldest {new Date(stats.oldestFailedAt).toLocaleDateString()})
                </span>
              )}
              , <strong>{stats.purgeable}</strong> settled &gt;{PURGE_CUTOFF_DAYS}d.
            </span>
            {stats.failed > 0 && (
              <button
                onClick={handleRetry}
                disabled={running !== null}
                className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
                style={{ backgroundColor: 'var(--accent)' }}
              >
                <RotateCcw size={14} />
                {running === 'retry' ? 'Re-queuing…' : `Retry ${stats.failed} failed`}
              </button>
            )}
            {stats.purgeable > 0 && (
              <button
                onClick={handlePurge}
                disabled={running !== null}
                className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
                style={{ backgroundColor: 'var(--danger, #dc2626)' }}
              >
                <Trash2 size={14} />
                {running === 'purge' ? 'Purging…' : `Purge ${stats.purgeable} old`}
              </button>
            )}
            <button
              onClick={() => setStats(null)}
              disabled={running !== null}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
              style={{ color: 'var(--text-muted)' }}
            >
              Cancel
            </button>
          </>
        )}

        {done && (
          <span className="inline-flex items-center gap-1 text-[13px]" style={{ color: 'var(--success)' }}>
            <Check size={14} />
            {done}
          </span>
        )}
      </div>

      {error && <CardError message={error} />}
    </section>
  );
}

// ── Shared bits ─────────────────────────────────────────────────────────────────────────────

function CardError({ message }: { message: string }) {
  return (
    <div
      className="text-[13px] rounded-lg px-3 py-2"
      style={{ color: 'var(--danger, #dc2626)', backgroundColor: 'var(--danger-muted, #fee2e2)' }}
    >
      {message}
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}
