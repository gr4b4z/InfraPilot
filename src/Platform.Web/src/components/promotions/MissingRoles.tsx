import { AlertTriangle } from 'lucide-react';
import { missingRolesLabel } from '@/lib/workItem';

/**
 * The "this work item has nobody answerable for it" affordance, in the two shapes the app needs.
 *
 * A promotion policy can require participant roles on every work item it gates (Settings → Promotion
 * policies → Required work-item roles). The server derives which of those roles are unfilled per work
 * item and returns them as `missingRoles`; these components are the single place that turns that list
 * into words, so the promotions list, the promotion page, the work-items queue and the work-item page
 * can't drift on what "incomplete" reads like.
 *
 * Deliberately warning-coloured rather than danger: the promotion is not blocked by an empty role, and
 * a red badge on every row of a newly-configured product would train people to ignore it.
 */

/**
 * Inline badge for a list row — "Needs QA Owner". Renders nothing when nothing is missing, so callers
 * can drop it in unconditionally.
 */
export function MissingRolesBadge({
  roles,
  size = 10,
}: {
  roles: string[] | undefined;
  size?: number;
}) {
  if (!roles || roles.length === 0) return null;
  const label = missingRolesLabel(roles);
  return (
    <span
      className="badge shrink-0"
      style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
      title={`Nobody is assigned as ${label} on this work item, and the promotion policy requires it.`}
    >
      <AlertTriangle size={size} />
      Needs {label}
    </span>
  );
}

/**
 * Row-level badge for a promotion card: "2 work items need attention". Used where the individual work
 * items aren't listed with their own badges (the promotions list), so the count is the whole message.
 */
export function WorkItemsNeedingAttentionBadge({
  count,
  roles,
}: {
  count: number;
  /** The distinct roles missing across those items, for the tooltip. */
  roles: string[];
}) {
  if (count <= 0) return null;
  const label = missingRolesLabel(roles);
  return (
    <span
      className="badge shrink-0 whitespace-nowrap"
      style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
      title={
        label
          ? `${count} work item${count === 1 ? '' : 's'} with nobody assigned as ${label}.`
          : `${count} work item${count === 1 ? '' : 's'} missing a required participant.`
      }
    >
      <AlertTriangle size={10} />
      {count} work item{count === 1 ? '' : 's'} need{count === 1 ? 's' : ''} attention
    </span>
  );
}

/**
 * Full-width notice for a detail surface: names the unfilled roles and asks for somebody to be put on
 * them. `action` is where the caller passes the sentence that points at its own assign control — the
 * wording differs between "there's an Assign button right below this" and "assign on the promotion".
 */
export function MissingRolesNotice({
  roles,
  action,
}: {
  roles: string[] | undefined;
  action?: string;
}) {
  if (!roles || roles.length === 0) return null;
  const label = missingRolesLabel(roles);
  const plural = roles.length === 1 ? 'role' : 'roles';
  return (
    <div
      className="flex items-start gap-2.5 rounded-lg border px-3 py-2 text-[12px]"
      style={{
        borderColor: 'var(--warning)',
        backgroundColor: 'var(--warning-bg)',
        color: 'var(--warning)',
      }}
    >
      <AlertTriangle size={14} style={{ flexShrink: 0, marginTop: 1 }} />
      <span>
        <span className="font-medium">Needs attention — no {label}.</span>{' '}
        The promotion policy requires somebody in {roles.length === 1 ? 'this' : 'these'} {plural} on
        every work item. {action ?? 'Assign someone to continue.'}
      </span>
    </div>
  );
}
