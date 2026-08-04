import { useMemo } from 'react';
import { AlertTriangle } from 'lucide-react';
import { roleDisplay } from '@/lib/roleLabel';
import { filterLabelClass, filterSelectClass } from '@/components/ui/FilterPanel';
import type { PendingAssignee } from '@/lib/api';

/**
 * Picker for narrowing the My-queue list by (role, person).
 *
 * Two side-by-side native selects:
 *   - Role: "Any role" + every configured participant role, ordered by label. Roles the queue
 *     carries but nobody configured are not offered — they can't be assigned to, so filtering on one
 *     only leads somewhere you can't act.
 *   - Person: "Anyone" + "Me" + the "nobody" option + each distinct person seen on the user's
 *     authorized list, filtered by the currently-selected role.
 *
 * Role and person compose into the question this filter exists to answer: pick a role, pick
 * "nobody assigned", and the list becomes the work items missing that role — the ones whose
 * sign-off has nobody to chase. The role list is the configured vocabulary rather than the roles
 * present in the data precisely because of that combination: a role nobody has been put on
 * anywhere would otherwise be the one choice the dropdown couldn't offer.
 *
 * Pure display narrowing — server-side authorisation (group membership, excluded role,
 * not-yet-decided) is unchanged. Person choices come from the user's queue itself (server returns
 * the (email, role) rollup pre-narrowing) so that dropdown never offers a zero-result pick.
 *
 * "Unassigned" and "Me" stay as person values, not separate modes — see
 * <see cref="MyQueuePage"/> for how the matrix maps to the API call.
 */
export type AssigneeFilterValue = {
  /** Canonical role key, or null for "any role". */
  role: string | null;
  /**
   * Person mode:
   *   - 'all'        — no person narrowing.
   *   - 'me'         — match current user's email.
   *   - 'unassigned' — nobody holds the role (or, with no role picked, none of the roles that
   *                    count as an assignment).
   *   - 'person'     — specific email + displayName.
   */
  mode: 'all' | 'me' | 'unassigned' | 'person';
  /** Set when mode === 'person'. */
  email?: string;
  /** Set when mode === 'person'. */
  displayName?: string;
};

const ANY_ROLE = '__any__';
const ANYONE = '__all__';
const ME = '__me__';
const UNASSIGNED = '__unassigned__';

export function AssigneeFilter({
  value,
  onChange,
  assignees,
  roles,
  hidePerson = false,
}: {
  value: AssigneeFilterValue;
  onChange: (next: AssigneeFilterValue) => void;
  /** (email, role) → count rollup from the queue endpoint. Empty when the queue is empty. */
  assignees: PendingAssignee[];
  /** Configured participant roles from the server. Rendered sorted by label. */
  roles: string[];
  /**
   * Drop the person select, keeping only the role one. Used on the "Assigned to me" tab, where
   * the person is fixed by the tab — offering a picker that could contradict it would make the
   * tab a lie. Role narrowing ("show me only the items where I'm the QA") still applies.
   */
  hidePerson?: boolean;
}) {
  // People to show in the person dropdown, filtered by the currently-selected role.
  // When role is null we dedupe on email and pick the displayName from the row with the
  // highest count (most representative). When role is set, we just keep the assignees with
  // that role since each (email, role) pair is already a unique server row.
  const people = useMemo(() => {
    if (value.role) {
      return assignees
        .filter((a) => a.role === value.role)
        .map((a) => ({ email: a.email, displayName: a.displayName }));
    }
    // Dedupe by email; the input is sorted by count desc so the first hit per email is the
    // best displayName.
    const seen = new Set<string>();
    const out: Array<{ email: string; displayName: string }> = [];
    for (const a of assignees) {
      if (seen.has(a.email)) continue;
      seen.add(a.email);
      out.push({ email: a.email, displayName: a.displayName });
    }
    return out;
  }, [assignees, value.role]);

  // Configured roles, ordered by the label on screen. The configured order is an admin's arrangement
  // of a settings list, which says nothing about how someone scans a dropdown for a role by name.
  const roleOptions = useMemo(
    () =>
      [...roles].sort((a, b) =>
        roleDisplay({ role: a }).localeCompare(roleDisplay({ role: b }), undefined, {
          sensitivity: 'base',
        }),
      ),
    [roles],
  );

  // A role the queue carries but nobody configured is deliberately NOT offered. It can't be
  // assigned to (the server refuses it), so as a filter it only ever leads to a dead end — the fix
  // is to add it under Settings → Participant Roles, which is what the chip on the item says.
  //
  // The active pick still gets an option when it isn't configured, because a select whose value has
  // no matching option renders as "Any role" while the filter is still narrowing, which reads as a
  // broken queue rather than an active filter. It's kept out of the list, not out of the select.
  const roleIsUnknown = !!value.role && !roles.includes(value.role);

  // "Nobody" reads differently depending on whether a role is in play: with one, it's that role's
  // slot that's empty; without one, it's the whole assignment.
  const unassignedLabel = value.role
    ? `No ${roleDisplay({ role: value.role })} assigned`
    : 'Nobody assigned';

  const personValue = useMemo(() => {
    if (value.mode === 'me') return ME;
    if (value.mode === 'unassigned') return UNASSIGNED;
    if (value.mode === 'person' && value.email) return `email:${value.email}`;
    return ANYONE;
  }, [value]);

  // The selected person may not be in `people` — the rollup is scoped to the rows the current tab
  // loaded, so switching tab or narrowing the role can drop them out of it. This used to display
  // "Anyone" in that case while `mode` stayed 'person' and the request kept sending their email: an
  // active filter with no visible sign of itself. Like the unconfigured role above, they get an option
  // so the control keeps saying what it is filtering by.
  const personIsOffList =
    value.mode === 'person'
    && !!value.email
    && !people.some((p) => p.email.toLowerCase() === value.email!.toLowerCase());

  const handleRoleChange = (next: string) => {
    const nextRole = next === ANY_ROLE ? null : next;
    // If the currently-selected person is no longer available under the new role, reset to
    // "Anyone". Special modes ('me', 'unassigned', 'all') always remain valid.
    if (value.mode === 'person' && nextRole) {
      const stillVisible = assignees.some(
        (a) => a.role === nextRole && a.email.toLowerCase() === (value.email ?? '').toLowerCase(),
      );
      if (!stillVisible) {
        onChange({ role: nextRole, mode: 'all' });
        return;
      }
    } else if (value.mode === 'person' && !nextRole) {
      // role=any with a specific person — person stays as-is so long as that email exists at all.
      const stillVisible = assignees.some(
        (a) => a.email.toLowerCase() === (value.email ?? '').toLowerCase(),
      );
      if (!stillVisible) {
        onChange({ role: null, mode: 'all' });
        return;
      }
    }
    onChange({ ...value, role: nextRole });
  };

  const handlePersonChange = (next: string) => {
    if (next === ANYONE) {
      onChange({ ...value, mode: 'all', email: undefined, displayName: undefined });
      return;
    }
    if (next === ME) {
      onChange({ ...value, mode: 'me', email: undefined, displayName: undefined });
      return;
    }
    if (next === UNASSIGNED) {
      onChange({ ...value, mode: 'unassigned', email: undefined, displayName: undefined });
      return;
    }
    if (next.startsWith('email:')) {
      const email = next.slice('email:'.length);
      const person = people.find((p) => p.email === email);
      if (!person) return;
      onChange({
        ...value,
        mode: 'person',
        email: person.email,
        displayName: person.displayName,
      });
    }
  };

  return (
    <div className="flex w-full flex-col gap-2 sm:inline-flex sm:w-auto sm:flex-row sm:items-center">
      <label
        className={filterLabelClass}
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Role</span>
        <select
          value={value.role ?? ANY_ROLE}
          onChange={(e) => handleRoleChange(e.target.value)}
          className={filterSelectClass}
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANY_ROLE}>Any role</option>
          {roleOptions.map((r) => (
            <option key={r} value={r}>
              {roleDisplay({ role: r })}
            </option>
          ))}
          {roleIsUnknown && (
            <option value={value.role!}>{value.role}</option>
          )}
        </select>
        {roleIsUnknown && (
          <span
            className="inline-flex items-center gap-1 text-[11px]"
            style={{ color: 'var(--warning)' }}
            title={`"${value.role}" is not a configured participant role. Add it under Settings → Participant Roles to assign people to it.`}
          >
            <AlertTriangle size={11} />
            Not configured
          </span>
        )}
      </label>

      {!hidePerson && (
      <label
        className={filterLabelClass}
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Assignee</span>
        <select
          value={personValue}
          onChange={(e) => handlePersonChange(e.target.value)}
          className={filterSelectClass}
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANYONE}>Anyone</option>
          <option value={ME}>Me</option>
          <option value={UNASSIGNED}>{unassignedLabel}</option>
          {people.length > 0 && (
            <optgroup label="On your queue">
              {people.map((p) => (
                <option key={p.email} value={`email:${p.email}`}>
                  {p.displayName}
                </option>
              ))}
            </optgroup>
          )}
          {personIsOffList && (
            <option value={`email:${value.email}`}>
              {value.displayName || value.email}
            </option>
          )}
        </select>
        {personIsOffList && (
          <span
            className="inline-flex items-center gap-1 text-[11px]"
            style={{ color: 'var(--text-muted)' }}
            title={`${value.displayName || value.email} holds no role on the work items in this view. The filter is still applied — switch to "Anyone" to clear it.`}
          >
            Not on this view
          </span>
        )}
      </label>
      )}
    </div>
  );
}

// ── localStorage helpers exported for MyQueuePage to keep persistence colocated ───────────
export const ASSIGNEE_FILTER_STORAGE_KEY = 'me.queue.assigneeFilter';

const DEFAULT_VALUE: AssigneeFilterValue = { role: null, mode: 'all' };

/**
 * Loads the persisted filter, or the default ("Any role" + "Anyone") when nothing valid is
 * stored. Migration of pre-role payloads is intentional: any old shape collapses to default,
 * since the role-aware shape is a superset and no information is lost from the user's perspective.
 */
export function loadAssigneeFilter(): AssigneeFilterValue {
  try {
    const raw = window.localStorage.getItem(ASSIGNEE_FILTER_STORAGE_KEY);
    if (!raw) return DEFAULT_VALUE;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return DEFAULT_VALUE;
    // New shape: must have explicit `role` field (string or null) AND a valid mode.
    if (!('role' in parsed)) return DEFAULT_VALUE;
    const role = parsed.role;
    if (role !== null && typeof role !== 'string') return DEFAULT_VALUE;
    const mode = parsed.mode;
    if (mode === 'all' || mode === 'me' || mode === 'unassigned') {
      return { role, mode };
    }
    if (
      mode === 'person' &&
      typeof parsed.email === 'string' &&
      typeof parsed.displayName === 'string'
    ) {
      return { role, mode, email: parsed.email, displayName: parsed.displayName };
    }
  } catch {
    // Ignore — corrupted entry; fall through to default.
  }
  return DEFAULT_VALUE;
}

export function saveAssigneeFilter(value: AssigneeFilterValue): void {
  try {
    window.localStorage.setItem(ASSIGNEE_FILTER_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // Ignore — quota or disabled storage; the page just won't persist across reloads.
  }
}
