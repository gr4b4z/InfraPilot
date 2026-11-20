import { useMemo } from 'react';
import { filterLabelClass, filterSelectClass } from '@/components/ui/FilterPanel';
import type { PendingAssignee } from '@/lib/api';
import { useRoleTeams } from '@/lib/roleLabel';

type Person = { email: string; displayName: string };

const byName = (a: Person, b: Person) =>
  a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' });

/**
 * Picker for narrowing the My-queue list by the person holding a policy-required role.
 *
 * One native select: "Anyone" + "Me" + "Missing a required role" + each role's team (Settings →
 * Participant Roles → Team), always — the QA team should be pickable whether or not they hold
 * anything right now, and picking one with nothing assigned is an honest empty list — then each
 * other distinct person seen holding a required role on the user's authorized list.
 *
 * "Required" is the promotion policy's `requiredWorkItemRoles` — the roles that make somebody
 * answerable for a work item. Picking a person narrows to the items where they hold one of those
 * roles (`roleRequirement=assigned` server-side), not the items their name merely appears on: a
 * ticket's reporter isn't being made answerable for it. This replaced the earlier (role, person)
 * pair of selects — the role a person is matched against is now the item's own policy's business,
 * not a dropdown pick.
 *
 * Pure display narrowing — server-side authorisation (group membership, excluded role,
 * not-yet-decided) is unchanged. The non-team choices come from the user's queue itself: the server
 * returns the (email, required-role) rollup pre-narrowing and already limited to required-role
 * holders, so none of those is a zero-result pick.
 */
export type AssigneeFilterValue = {
  /**
   * Person mode:
   *   - 'all'        — no narrowing.
   *   - 'me'         — current user holds a policy-required role.
   *   - 'unassigned' — at least one policy-required role has nobody in it.
   *   - 'person'     — specific email + displayName holds a policy-required role.
   */
  mode: 'all' | 'me' | 'unassigned' | 'person';
  /** Set when mode === 'person'. */
  email?: string;
  /** Set when mode === 'person'. */
  displayName?: string;
};

const ANYONE = '__all__';
const ME = '__me__';
const UNASSIGNED = '__unassigned__';

export function AssigneeFilter({
  value,
  onChange,
  assignees,
}: {
  value: AssigneeFilterValue;
  onChange: (next: AssigneeFilterValue) => void;
  /** (email, required-role) → count rollup from the queue endpoint. Empty when the queue is empty. */
  assignees: PendingAssignee[];
}) {
  const teams = useRoleTeams();

  // Each role's team (Settings → Participant Roles → Team), always listed — whether or not they hold
  // anything on this queue right now — in alphabetical order so a person finds themselves in the same
  // place every time. Somebody on two teams is listed under the first.
  const teamGroups = useMemo(() => {
    const seen = new Set<string>();
    const groups: Array<{ key: string; label: string; people: Person[] }> = [];
    for (const team of teams) {
      const people: Person[] = [];
      for (const m of team.members) {
        const id = m.email.trim().toLowerCase();
        if (seen.has(id)) continue;
        seen.add(id);
        people.push({ email: m.email.trim(), displayName: m.displayName || m.email });
      }
      people.sort(byName);
      if (people.length > 0) groups.push({ key: team.key, label: `${team.displayName} team`, people });
    }
    return { groups, emails: seen };
  }, [teams]);

  // Everyone else holding a required role on the queue, deduped by email. The input is sorted by
  // count desc so the first hit per email is the best displayName.
  const others = useMemo(() => {
    const seen = new Set<string>(teamGroups.emails);
    const out: Person[] = [];
    for (const a of assignees) {
      const id = a.email.trim().toLowerCase();
      if (seen.has(id)) continue;
      seen.add(id);
      out.push({ email: a.email, displayName: a.displayName });
    }
    return out;
  }, [assignees, teamGroups]);

  const people = useMemo(
    () => [...teamGroups.groups.flatMap((g) => g.people), ...others],
    [teamGroups, others],
  );

  const personValue = useMemo(() => {
    if (value.mode === 'me') return ME;
    if (value.mode === 'unassigned') return UNASSIGNED;
    if (value.mode === 'person' && value.email) return `email:${value.email}`;
    return ANYONE;
  }, [value]);

  // The selected person may not be in `people` — the rollup is scoped to the rows the current tab
  // loaded, so switching tab can drop them out of it. This used to display "Anyone" in that case
  // while `mode` stayed 'person' and the request kept sending their email: an active filter with no
  // visible sign of itself. They get an option so the control keeps saying what it is filtering by.
  const personIsOffList =
    value.mode === 'person'
    && !!value.email
    && !people.some((p) => p.email.toLowerCase() === value.email!.toLowerCase());

  const handlePersonChange = (next: string) => {
    if (next === ANYONE) return onChange({ mode: 'all' });
    if (next === ME) return onChange({ mode: 'me' });
    if (next === UNASSIGNED) return onChange({ mode: 'unassigned' });
    if (next.startsWith('email:')) {
      const email = next.slice('email:'.length);
      const person = people.find((p) => p.email === email);
      if (!person) return;
      onChange({ mode: 'person', email: person.email, displayName: person.displayName });
    }
  };

  return (
    <label
      className={filterLabelClass}
      style={{ color: 'var(--text-muted)' }}
      title="Matches people against the roles the item's promotion policy requires — not every role their name appears in."
    >
      <span>Assigned to</span>
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
        <option value={UNASSIGNED}>Missing a required role</option>
        {teamGroups.groups.map((g) => (
          <optgroup key={g.key} label={g.label}>
            {g.people.map((p) => (
              <option key={p.email} value={`email:${p.email}`}>
                {p.displayName}
              </option>
            ))}
          </optgroup>
        ))}
        {others.length > 0 && (
          <optgroup label={teamGroups.groups.length > 0 ? 'Also on your queue' : 'On your queue'}>
            {others.map((p) => (
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
          title={`${value.displayName || value.email} holds no required role on the work items in this view. The filter is still applied — switch to "Anyone" to clear it.`}
        >
          Not on this view
        </span>
      )}
    </label>
  );
}

// ── localStorage helpers exported for MyQueuePage to keep persistence colocated ───────────
export const ASSIGNEE_FILTER_STORAGE_KEY = 'me.queue.assigneeFilter';

const DEFAULT_VALUE: AssigneeFilterValue = { mode: 'all' };

/**
 * Loads the persisted filter, or the default ("Anyone") when nothing valid is stored. Older
 * payloads carried a `role` field alongside the mode (the removed role select); the mode is kept
 * and the role simply ignored, so nobody's saved person pick is lost to the migration.
 */
export function loadAssigneeFilter(): AssigneeFilterValue {
  try {
    const raw = window.localStorage.getItem(ASSIGNEE_FILTER_STORAGE_KEY);
    if (!raw) return DEFAULT_VALUE;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return DEFAULT_VALUE;
    const mode = parsed.mode;
    if (mode === 'all' || mode === 'me' || mode === 'unassigned') {
      return { mode };
    }
    if (
      mode === 'person' &&
      typeof parsed.email === 'string' &&
      typeof parsed.displayName === 'string'
    ) {
      return { mode, email: parsed.email, displayName: parsed.displayName };
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
