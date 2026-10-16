import { AlertTriangle, Info } from 'lucide-react';

/**
 * The two rendered shapes of "what does pressing Approve actually do?" — see
 * {@link approvalDeploys} in `@/lib/approvalEffect` for the rule and the reasoning behind the
 * wording. Components only, so this file stays hot-reloadable.
 */

/**
 * Full-width notice for the approval card. Rendered for both answers rather than only the alarming
 * one: an approver who sees nothing learns nothing, and "no warning" is not a reading of "this will
 * not deploy on its own".
 */
export function ApprovalEffectNotice({
  deploys,
  version,
  targetEnv,
}: {
  deploys: boolean;
  version: string;
  targetEnv: string;
}) {
  const Icon = deploys ? AlertTriangle : Info;
  return (
    <div
      className="flex items-start gap-2.5 rounded-lg border px-3 py-2 text-[12px]"
      style={{
        borderColor: deploys ? 'var(--warning)' : 'var(--border-color)',
        backgroundColor: deploys ? 'var(--warning-bg)' : 'var(--info-bg)',
        color: deploys ? 'var(--warning)' : 'var(--info)',
      }}
    >
      <Icon size={14} style={{ flexShrink: 0, marginTop: 1 }} />
      {deploys ? (
        <span>
          <span className="font-medium">Approving here deploys this.</span> The gate on this edge is
          the only gate — as soon as it is satisfied the release automation rolls v{version} out to{' '}
          <span className="font-medium">{targetEnv}</span>. Nothing outside InfraPortal has to say
          yes.
        </span>
      ) : (
        <span>
          <span className="font-medium">Approving does not deploy this on its own.</span> Once the
          gate here is satisfied, v{version} is handed to the deployment pipeline, which stops at an
          approval of its own before <span className="font-medium">{targetEnv}</span> changes —
          somebody still has to let it through there.
        </span>
      )}
    </div>
  );
}

/**
 * Compact line for a list, where several promotions are being approved at once and the version and
 * environment of each are on the rows themselves. Says nothing when none of the selected promotions
 * deploys by itself — there the pipeline's own approval is the next stop and the rows already say so.
 */
export function BulkApprovalEffectLine({
  deployingCount,
  totalCount,
}: {
  deployingCount: number;
  totalCount: number;
}) {
  if (deployingCount === 0) return null;
  const all = deployingCount === totalCount;
  return (
    <span
      className="flex items-center gap-1.5 text-[12px]"
      style={{ color: 'var(--warning)' }}
    >
      <AlertTriangle size={12} className="shrink-0" />
      {all
        ? `Approving ${totalCount === 1 ? 'this' : `these ${totalCount}`} releases the deploy — nothing outside InfraPortal has to say yes.`
        : `${deployingCount} of ${totalCount} deploy on approval — nothing outside InfraPortal has to say yes.`}
    </span>
  );
}
