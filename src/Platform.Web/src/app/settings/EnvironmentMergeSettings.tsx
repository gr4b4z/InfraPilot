import { useEffect, useMemo, useState } from 'react';
import { ArrowRight, Check, GitMerge, RefreshCw } from 'lucide-react';
import { api } from '@/lib/api';
import type { EnvironmentMergePlan, EnvironmentUsage } from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * Settings → Environments → Merge: folds several names for one environment into a single one.
 *
 * Aliases (the editor above) are forward-only — they fix what arrives next and leave the years that
 * arrived under the old name where they are. Until the history follows, the deployment matrix still
 * shows two production columns and analytics still counts two pipelines. This is the second,
 * reviewed step, and the page is shaped around that split: the table says what names the data
 * actually uses, and the merge below moves the rows and records the alias in one go.
 */
export function EnvironmentMergeSettings() {
  const [usage, setUsage] = useState<EnvironmentUsage[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Bumped after a merge so the table and the settings store reload through one path.
  const [reloadTick, setReloadTick] = useState(0);
  const loadSettings = useSettingsStore((s) => s.load);
  // The `resolvesTo` column is computed server-side from the configured aliases, so saving the
  // editor above changes what this table should say. Subscribing to the store's environments is
  // what makes "add the alias" and "see it took effect" one action instead of a reload.
  const environments = useSettingsStore((s) => s.environments);

  useEffect(() => {
    let cancelled = false;
    api
      .listEnvironmentUsage()
      .then((rows) => {
        if (!cancelled) setUsage(rows);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setLoadError(e instanceof Error ? e.message : 'Failed to load environment usage');
        setUsage([]);
      });
    return () => {
      cancelled = true;
    };
  }, [reloadTick, environments]);

  const reload = () => {
    // The merge rewrites the settings row too (alias recorded, source environment dropped), so the
    // store has to rehydrate or the editor above keeps showing the environment that just went away.
    void loadSettings();
    setReloadTick((t) => t + 1);
  };

  return (
    <section
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Merge environments
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Every environment name your stored data actually uses, as opposed to the ones configured
          above. Three rows for <code>dev</code>, <code>develop</code> and <code>development</code>{' '}
          means three pipelines naming the same environment differently — merging folds their
          deployments, promotions, sign-offs, release notes, rollbacks and webhook filters onto one
          key, records the others as aliases so new traffic keeps landing there, and removes the
          duplicates from the list above.
        </p>
      </div>

      {usage === null ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Loading…
        </p>
      ) : (
        <>
          <UsageTable rows={usage} />
          <MergeForm rows={usage} onMerged={reload} />
        </>
      )}

      {loadError && (
        <p className="text-[13px]" style={{ color: 'var(--danger)' }}>
          {loadError}
        </p>
      )}
    </section>
  );
}

function UsageTable({ rows }: { rows: EnvironmentUsage[] }) {
  if (rows.length === 0) {
    return (
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
        No environments in the data yet — nothing has been deployed.
      </p>
    );
  }

  return (
    <div className="overflow-x-auto rounded-lg border" style={{ borderColor: 'var(--border-color)' }}>
      <table className="w-full text-[12px]">
        <thead>
          <tr
            className="text-left text-[11px] uppercase tracking-wider"
            style={{ color: 'var(--text-muted)' }}
          >
            <th className="px-3 py-2 font-semibold">Name in the data</th>
            <th className="px-3 py-2 font-semibold">Deploys</th>
            <th className="px-3 py-2 font-semibold">Promotions</th>
            <th className="px-3 py-2 font-semibold">Release notes</th>
            <th className="px-3 py-2 font-semibold">Last deploy</th>
            <th className="px-3 py-2 font-semibold">Status</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr
              key={row.key}
              className="border-t"
              style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
            >
              <td className="px-3 py-2 whitespace-nowrap">
                <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
                  {row.key}
                </span>
              </td>
              <td className="px-3 py-2 whitespace-nowrap">{row.deployments}</td>
              <td className="px-3 py-2 whitespace-nowrap">{row.promotions}</td>
              <td className="px-3 py-2 whitespace-nowrap">{row.releaseNotes}</td>
              <td className="px-3 py-2 whitespace-nowrap">
                {row.lastDeployedAt ? new Date(row.lastDeployedAt).toLocaleDateString() : '—'}
              </td>
              {/* The one column worth reading first: `resolvesTo` means the alias is already in
                  place and only the history is outstanding, which is exactly what this page fixes. */}
              <td className="px-3 py-2">
                {row.resolvesTo ? (
                  <span style={{ color: 'var(--warning, #d97706)' }}>
                    aliased to {row.resolvesTo} — history not merged
                  </span>
                ) : row.configured ? (
                  <span style={{ color: 'var(--text-muted)' }}>configured</span>
                ) : (
                  <span style={{ color: 'var(--text-muted)' }}>not configured</span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Preview-then-apply, following the contract the Maintenance cards and the service-product remap
 * use: a read-only count first, an explicit apply second, and the same shape reported back
 * afterwards so what happened can be compared with what was approved.
 */
function MergeForm({ rows, onMerged }: { rows: EnvironmentUsage[]; onMerged: () => void }) {
  const [into, setInto] = useState('');
  const [from, setFrom] = useState<string[]>([]);
  const [recordAliases, setRecordAliases] = useState(true);
  const [preview, setPreview] = useState<EnvironmentMergePlan | null>(null);
  const [result, setResult] = useState<EnvironmentMergePlan | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const keys = useMemo(() => rows.map((r) => r.key), [rows]);
  const sources = useMemo(
    () => from.filter((f) => f !== into).filter((f) => f.trim() !== ''),
    [from, into],
  );

  // Any change to the request invalidates the counts that were shown for the previous one.
  const resetOutcome = () => {
    setPreview(null);
    setResult(null);
    setError(null);
  };

  const toggleFrom = (key: string) => {
    resetOutcome();
    setFrom((prev) => (prev.includes(key) ? prev.filter((k) => k !== key) : [...prev, key]));
  };

  const run = async (apply: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const body = { into: into.trim(), from: sources, recordAliases };
      if (apply) {
        setResult(await api.mergeEnvironments(body));
        setPreview(null);
        setFrom([]);
        onMerged();
      } else {
        setPreview(await api.previewEnvironmentMerge(body));
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : apply ? 'Failed to merge' : 'Failed to preview');
    } finally {
      setBusy(false);
    }
  };

  const canRun = into.trim() !== '' && sources.length > 0;
  const shown = result ?? preview;

  return (
    <div
      className="rounded-lg border p-4 space-y-3"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="space-y-1.5">
        <span className={labelClass} style={labelStyle}>
          Merge these
        </span>
        {/* Checkboxes over a multi-select: the whole point is to see several near-identical names at
            once and tick the ones that mean the same thing. */}
        <div className="flex flex-wrap gap-1.5">
          {keys.map((key) => {
            const selected = from.includes(key);
            const isTarget = key === into.trim();
            return (
              <button
                key={key}
                type="button"
                onClick={() => toggleFrom(key)}
                disabled={isTarget}
                aria-pressed={selected}
                title={isTarget ? 'This is the target environment' : undefined}
                className="font-mono text-[12px] px-2 py-1 rounded-lg border transition-colors disabled:opacity-40"
                style={{
                  borderColor: selected ? 'var(--accent)' : 'var(--border-color)',
                  backgroundColor: selected ? 'var(--accent-muted)' : 'var(--bg-secondary)',
                  color: selected ? 'var(--accent)' : 'var(--text-secondary)',
                }}
              >
                {key}
              </button>
            );
          })}
        </div>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1">
          <span className={labelClass} style={labelStyle}>
            Into
          </span>
          {/* Free text, not a picker: the target may be a name that does not exist yet ("prod" when
              the data only has "production" and "productions"), and typing it is the whole answer. */}
          <input
            type="text"
            list="environment-merge-targets"
            value={into}
            onChange={(e) => {
              resetOutcome();
              setInto(e.target.value);
            }}
            placeholder="e.g. prod"
            spellCheck={false}
            className={`${inputClass} font-mono w-48`}
            style={inputStyle}
          />
          <datalist id="environment-merge-targets">
            {keys.map((key) => (
              <option key={key} value={key} />
            ))}
          </datalist>
        </label>

        <label
          className="flex items-center gap-2 text-[13px] pb-1.5"
          style={{ color: 'var(--text-secondary)' }}
          title="Without this, the next pipeline run recreates the environment you just merged away"
        >
          <input
            type="checkbox"
            checked={recordAliases}
            onChange={(e) => {
              resetOutcome();
              setRecordAliases(e.target.checked);
            }}
            className="accent-[var(--accent)]"
          />
          Record the merged names as aliases
        </label>

        <button
          type="button"
          onClick={() => void run(false)}
          disabled={!canRun || busy}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80 disabled:opacity-50"
          style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
        >
          <RefreshCw size={14} />
          {busy && !result ? 'Counting…' : 'Preview'}
        </button>
      </div>

      {!recordAliases && (
        <p className="text-[13px]" style={{ color: 'var(--warning, #d97706)' }}>
          Without recording aliases, the pipelines still posting the old names will recreate those
          environments on their next run and the history will split again.
        </p>
      )}

      {shown && <MergeCounts plan={shown} />}

      <div className="flex items-center gap-3 flex-wrap">
        {result ? (
          <span
            className="inline-flex items-center gap-1 text-[13px]"
            style={{ color: 'var(--success)' }}
          >
            <Check size={14} /> Merged
          </span>
        ) : (
          preview && (
            <button
              type="button"
              onClick={() => void run(true)}
              disabled={busy}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              <GitMerge size={14} />
              {busy ? 'Merging…' : `Merge into ${preview.into}`}
            </button>
          )
        )}
        {error && (
          <span className="text-[13px]" style={{ color: 'var(--danger)' }}>
            {error}
          </span>
        )}
      </div>
    </div>
  );
}

function MergeCounts({ plan }: { plan: EnvironmentMergePlan }) {
  const c = plan.counts;
  const past = plan.applied;

  return (
    <div className="space-y-2">
      <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
        {past ? 'Merged' : 'Would merge'}{' '}
        <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
          {plan.sources.join(', ')}
        </span>{' '}
        <ArrowRight size={12} className="inline" />{' '}
        <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
          {plan.into}
        </span>
        .
      </p>

      {plan.moved === 0 ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Nothing to move — no stored rows use those names.
        </p>
      ) : (
        <ul className="text-[13px] space-y-0.5" style={{ color: 'var(--text-secondary)' }}>
          <li>
            <strong>{c.deployments}</strong> deployment{c.deployments === 1 ? '' : 's'}
          </li>
          <li>
            <strong>{c.promotionCandidates}</strong> promotion
            {c.promotionCandidates === 1 ? '' : 's'}
            {c.promotionWorkItems > 0 && <> (+{c.promotionWorkItems} ticket links)</>}
            {c.openPromotionCandidates > 0 && (
              <span style={{ color: 'var(--warning, #d97706)' }}>
                {' '}
                — {c.openPromotionCandidates} still in flight
              </span>
            )}
          </li>
          <li>
            <strong>{c.workItemApprovals}</strong> ticket sign-off
            {c.workItemApprovals === 1 ? '' : 's'}
            {c.workItemComments > 0 && <> (+{c.workItemComments} comments)</>}
          </li>
          <li>
            <strong>{c.promotionPolicies + c.rollbackPolicies}</strong> polic
            {c.promotionPolicies + c.rollbackPolicies === 1 ? 'y' : 'ies'}
          </li>
          <li>
            <strong>{c.releaseNotes}</strong> release note{c.releaseNotes === 1 ? '' : 's'}
            {c.releaseNoteTemplates > 0 && <> (+{c.releaseNoteTemplates} templates)</>}
          </li>
          <li>
            <strong>{c.rollbackRequests}</strong> rollback{c.rollbackRequests === 1 ? '' : 's'}
            {c.webhookSubscriptions > 0 && (
              <> , <strong>{c.webhookSubscriptions}</strong> webhook filter
                {c.webhookSubscriptions === 1 ? '' : 's'}</>
            )}
          </li>
        </ul>
      )}

      {/* Spelled out rather than rolled into one number: each kind of leftover needs a different
          decision from the admin, and a bare "12 rows left behind" tells them nothing. */}
      {plan.leftBehind > 0 && (
        <div className="text-[13px] space-y-1" style={{ color: 'var(--warning, #d97706)' }}>
          <p>
            {plan.leftBehind} row{plan.leftBehind === 1 ? '' : 's'}{' '}
            {past ? 'stayed' : 'will stay'} where {plan.leftBehind === 1 ? 'it is' : 'they are'}:
          </p>
          <ul className="space-y-0.5 pl-4 list-disc">
            {c.promotionPolicyConflicts > 0 && (
              <li>
                {c.promotionPolicyConflicts} promotion polic
                {c.promotionPolicyConflicts === 1 ? 'y' : 'ies'} — {plan.into} already has a policy
                for that edge, and its policy is the one that governs from now on. Delete the
                duplicate in Settings → Promotions.
              </li>
            )}
            {c.rollbackPolicyConflicts > 0 && (
              <li>
                {c.rollbackPolicyConflicts} rollback polic
                {c.rollbackPolicyConflicts === 1 ? 'y' : 'ies'} — {plan.into} already has one for
                that product.
              </li>
            )}
            {c.workItemApprovalConflicts > 0 && (
              <li>
                {c.workItemApprovalConflicts} ticket sign-off
                {c.workItemApprovalConflicts === 1 ? '' : 's'} — the same approver already decided
                the same ticket against {plan.into}. The two decisions can disagree, so neither is
                overwritten.
              </li>
            )}
            {c.releaseNoteTemplateConflicts > 0 && (
              <li>
                {c.releaseNoteTemplateConflicts} release-note template
                {c.releaseNoteTemplateConflicts === 1 ? '' : 's'} — {plan.into} already has one for
                that product.
              </li>
            )}
            {c.degenerateEdges > 0 && (
              <li>
                {c.degenerateEdges} promotion{c.degenerateEdges === 1 ? '' : 's'} or polic
                {c.degenerateEdges === 1 ? 'y' : 'ies'} whose two ends would become the same
                environment. A promotion from an environment to itself can't be represented — review
                and delete these by hand.
              </li>
            )}
          </ul>
        </div>
      )}

      {plan.applied && plan.aliasesRecorded && (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          {plan.sources.join(', ')} {plan.sources.length === 1 ? 'is' : 'are'} now{' '}
          {plan.sources.length === 1 ? 'an alias' : 'aliases'} of {plan.into}
          {plan.removedEnvironments.length > 0 && (
            <>
              , and {plan.removedEnvironments.join(', ')}{' '}
              {plan.removedEnvironments.length === 1 ? 'was' : 'were'} removed from the environment
              list
            </>
          )}
          .
        </p>
      )}
    </div>
  );
}
