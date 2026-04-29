import { useMemo } from 'react';
import { roleDisplay } from '@/lib/roleLabel';
import type { PendingAssignee } from '@/lib/api';

/**
 * Picker for narrowing the My-queue list by (role, person).
 *
 * Two side-by-side native selects:
 *   - Role: "Any role" + each role from the server-supplied canonical role set.
 *   - Person: "Anyone" + "Me" + "Unassigned" + each distinct person seen on the user's
 *     authorized list, filtered by the currently-selected role.
 *
 * Pure display narrowing — server-side authorisation (group membership, excluded role,
 * not-yet-decided) is unchanged. Choices come from the user's queue itself (server returns
 * the (email, role) rollup pre-narrowing) so the dropdowns never offer a zero-result pick.
 *
 * "Unassigned" and "Me" stay as person values, not separate modes — see
 * <see cref="MyQueuePage"/> for how the matrix maps to the API call.
 */
export type AssigneeFilterValue = {
  /** Canonical role from the server's role set, or null for "any role". */
  role: string | null;
  /**
   * Person mode:
   *   - 'all'        — no person narrowing.
   *   - 'me'         — match current user's email.
   *   - 'unassigned' — no participant in the effective role set.
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
}: {
  value: AssigneeFilterValue;
  onChange: (next: AssigneeFilterValue) => void;
  /** (email, role) → count rollup from the queue endpoint. Empty when the queue is empty. */
  assignees: PendingAssignee[];
  /** Canonical assignee-role set from the server. */
  roles: string[];
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

  // If the current person pick is no longer available (e.g. role narrowed and they don't
  // appear in the new set), reset to "Anyone" rather than silently render an invalid value.
  const personValue = useMemo(() => {
    if (value.mode === 'all') return ANYONE;
    if (value.mode === 'me') return ME;
    if (value.mode === 'unassigned') return UNASSIGNED;
    if (value.mode === 'person') {
      const stillVisible = people.some(
        (p) => p.email.toLowerCase() === (value.email ?? '').toLowerCase(),
      );
      return stillVisible ? `email:${value.email}` : ANYONE;
    }
    return ANYONE;
  }, [value, people]);

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
    <div className="inline-flex items-center gap-2">
      <label
        className="inline-flex items-center gap-1.5 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Role</span>
        <select
          value={value.role ?? ANY_ROLE}
          onChange={(e) => handleRoleChange(e.target.value)}
          className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANY_ROLE}>Any role</option>
          {roles.map((r) => (
            <option key={r} value={r}>
              {roleDisplay({ role: r })}
            </option>
          ))}
        </select>
      </label>

      <label
        className="inline-flex items-center gap-1.5 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <span>Assignee</span>
        <select
          value={personValue}
          onChange={(e) => handlePersonChange(e.target.value)}
          className="rounded-lg border px-2 py-1.5 text-[12px] font-medium"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-primary)',
          }}
        >
          <option value={ANYONE}>Anyone</option>
          <option value={ME}>Me</option>
          <option value={UNASSIGNED}>Unassigned</option>
          {people.length > 0 && (
            <optgroup label="On your queue">
              {people.map((p) => (
                <option key={p.email} value={`email:${p.email}`}>
                  {p.displayName}
                </option>
              ))}
            </optgroup>
          )}
        </select>
      </label>
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
