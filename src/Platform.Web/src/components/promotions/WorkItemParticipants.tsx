import { useEffect, useMemo, useState } from 'react';
import { api } from '@/lib/api';
import type { PromotionSourceEventParticipant } from '@/lib/api';
import { roleDisplay } from '@/lib/roleLabel';
import { formatReferenceParticipant } from '@/lib/workItem';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { Plus, Users, X } from 'lucide-react';

/**
 * The people assigned to one work-item reference of a promotion candidate, with assign / reassign /
 * remove controls. Writes go to
 * `PATCH /api/promotions/{candidateId}/references/{key}/participants` — candidates are
 * self-contained (there is no deploy event to override), so the candidate id is the write target.
 *
 * Shared by the promotion detail page's work-item rows and the work-item detail page so the two
 * surfaces can't drift on what assignment means or which endpoint it hits.
 */

export function WorkItemParticipants({
  candidateId,
  referenceKey,
  participants,
  onChanged,
  readOnly = false,
  layout = 'chips',
}: {
  /** Candidate the reference belongs to. Null when it can't be resolved — controls are hidden. */
  candidateId: string | null;
  referenceKey: string;
  participants: PromotionSourceEventParticipant[];
  /** Called after a successful write so the parent can refetch. */
  onChanged: () => void;
  readOnly?: boolean;
  /** `chips` = one wrapping row (inside a list row); `rows` = one line per role (inside a card). */
  layout?: 'chips' | 'rows';
}) {
  // editingRole === '' means "new assign" (role chosen inside the picker).
  // editingRole === <role> means reassigning that specific slot.
  const [editingRole, setEditingRole] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const editable = !readOnly && !!candidateId && !!referenceKey;

  const submit = async (role: string, assignee: { email: string; displayName: string } | null) => {
    if (!candidateId) return;
    setBusy(true);
    setError(null);
    try {
      await api.assignPromotionReferenceParticipant(candidateId, referenceKey, role, assignee);
      setEditingRole(null);
      onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update participant');
    } finally {
      setBusy(false);
    }
  };

  const newAssignOpen = editingRole === '';

  // Read-only. Chips still wrap and show every assignee in full — collapsing them into one
  // truncated line hid most of the list, which is exactly what you need to see here. Renders
  // nothing at all in chip layout when there is nobody to show, so an empty slot adds no weight.
  if (!editable) {
    if (participants.length === 0) {
      return layout === 'rows' ? (
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          No people assigned.
        </p>
      ) : null;
    }
    if (layout === 'rows') {
      return (
        <div className="space-y-2">
          {participants.map((p, i) => (
            <ParticipantRow key={`${p.role}-${i}`} participant={p} />
          ))}
        </div>
      );
    }
    return (
      <div className="mt-1 flex flex-wrap items-center gap-1.5">
        {participants.map((p, i) => (
          <span
            key={`${p.role}-${i}`}
            className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
            style={{
              backgroundColor: 'var(--bg-tertiary, var(--bg-primary))',
              color: 'var(--text-secondary)',
              border: '1px solid var(--border-color)',
            }}
            title={formatReferenceParticipant(p)}
          >
            <Users size={10} style={{ flexShrink: 0 }} />
            <span>
              {roleDisplay(p)}: {p.displayName ?? p.email ?? '—'}
            </span>
          </span>
        ))}
      </div>
    );
  }

  if (layout === 'rows') {
    return (
      <div className="space-y-2">
        {participants.length === 0 && !newAssignOpen && (
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            No people assigned.
          </p>
        )}
        {participants.map((p, i) => (
          <ParticipantRow
            key={`${p.role}-${i}`}
            participant={p}
            editing={editingRole === p.role}
            busy={busy}
            onReassign={() => setEditingRole(p.role)}
            onCancelEdit={() => setEditingRole(null)}
            onRemove={() => submit(p.role, null)}
            onPick={(picked) => submit(p.role, picked)}
          />
        ))}
        {error && (
          <p className="text-[11px]" style={{ color: 'var(--danger)' }}>
            {error}
          </p>
        )}
        <span className="inline-flex items-center relative">
          <button
            type="button"
            onClick={() => setEditingRole(newAssignOpen ? null : '')}
            className="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-[11px] font-medium transition-opacity hover:opacity-80"
            style={{ border: '1px dashed var(--border-color)', color: 'var(--text-muted)' }}
            disabled={busy}
          >
            <Plus size={11} /> Assign person
          </button>
          {newAssignOpen && (
            <InlineUserPicker
              role={null}
              onPick={(picked) =>
                submit(picked.role, { email: picked.email, displayName: picked.displayName })
              }
              onCancel={() => setEditingRole(null)}
              busy={busy}
            />
          )}
        </span>
      </div>
    );
  }

  return (
    <div className="mt-1 flex flex-wrap items-center gap-1.5">
      {participants.map((p) => (
        <ParticipantChip
          key={`${p.role}-${p.email ?? ''}`}
          participant={p}
          onReassign={() => setEditingRole(p.role)}
          onClear={() => submit(p.role, null)}
          editing={editingRole === p.role}
          onCancelEdit={() => setEditingRole(null)}
          onPick={(picked) => submit(p.role, picked)}
          busy={busy}
        />
      ))}
      <span className="inline-flex items-center relative">
        <button
          type="button"
          onClick={() => setEditingRole(newAssignOpen ? null : '')}
          className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)', border: '1px dashed var(--border-color)' }}
          disabled={busy}
          title="Assign a person to this work item"
        >
          <Plus size={10} /> Assign
        </button>
        {newAssignOpen && (
          <InlineUserPicker
            role={null}
            onPick={(picked) =>
              submit(picked.role, { email: picked.email, displayName: picked.displayName })
            }
            onCancel={() => setEditingRole(null)}
            busy={busy}
          />
        )}
      </span>
      {error && (
        <span className="text-[10px]" style={{ color: 'var(--danger)' }}>
          {error}
        </span>
      )}
    </div>
  );
}

/**
 * One "Role — Person" line for the card layout. Read-only when no handlers are supplied, which is
 * how the same component serves both the editable and the view-only case.
 *
 * Editing is click-the-person, not a trailing button pair: an "Edit" link plus an "x" beside a
 * two-line block never sat on a sensible baseline, and the person's name is the obvious affordance
 * anyway. Removal moves inside the picker, where it reads as one of the things you can do to this
 * assignment rather than a stray destructive icon a mis-click away from the edit link.
 */
function ParticipantRow({
  participant,
  editing = false,
  busy = false,
  onReassign,
  onCancelEdit,
  onRemove,
  onPick,
}: {
  participant: PromotionSourceEventParticipant;
  editing?: boolean;
  busy?: boolean;
  onReassign?: () => void;
  onCancelEdit?: () => void;
  onRemove?: () => void;
  onPick?: (picked: { email: string; displayName: string }) => void;
}) {
  const overridden = participant.isOverride === true;
  const editable = !!onReassign && !!onRemove;
  const label = participant.displayName ?? participant.email ?? '—';

  const body = (
    <>
      <div className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
        {roleDisplay(participant)}
        {overridden && participant.assignedBy && (
          <span title={`Overridden by ${participant.assignedBy}`}> · overridden</span>
        )}
      </div>
      <div
        className="text-[13px] font-medium flex items-center gap-1.5 min-w-0"
        style={{ color: 'var(--text-primary)' }}
      >
        <span className="truncate">{label}</span>
        {/* Sits outside the clickable region below, so copying an address never opens the picker. */}
        <CopyEmailButton email={participant.email ?? null} />
      </div>
    </>
  );

  if (!editable) {
    return <div className="min-w-0">{body}</div>;
  }

  return (
    <div className="relative min-w-0">
      <div
        role="button"
        tabIndex={busy ? -1 : 0}
        onClick={onReassign}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            onReassign?.();
          }
        }}
        className="-mx-2 px-2 py-1 rounded-lg cursor-pointer transition-colors hover:bg-[var(--bg-secondary)] min-w-0"
        style={{ opacity: busy ? 0.6 : 1 }}
        title={`Reassign or remove the ${roleDisplay(participant)}`}
        aria-label={`Edit ${roleDisplay(participant)} ${label}`}
      >
        {body}
      </div>
      {editing && onPick && onCancelEdit && (
        <InlineUserPicker
          role={participant.role}
          onPick={(picked) => onPick({ email: picked.email, displayName: picked.displayName })}
          onCancel={onCancelEdit}
          onRemove={onRemove}
          removeLabel={overridden ? 'Remove assignment' : 'Hide for this work item'}
          busy={busy}
        />
      )}
    </div>
  );
}

function ParticipantChip({
  participant,
  onReassign,
  onClear,
  editing,
  onCancelEdit,
  onPick,
  busy,
}: {
  participant: PromotionSourceEventParticipant;
  onReassign: () => void;
  onClear: () => void;
  editing: boolean;
  onCancelEdit: () => void;
  onPick: (picked: { email: string; displayName: string }) => void;
  busy: boolean;
}) {
  const [menuOpen, setMenuOpen] = useState(false);
  const overridden = participant.isOverride === true;
  const tooltip = overridden && participant.assignedBy
    ? `${formatReferenceParticipant(participant)} (overridden by ${participant.assignedBy})`
    : formatReferenceParticipant(participant);

  return (
    <span className="inline-flex items-center relative">
      <button
        type="button"
        onClick={() => setMenuOpen((v) => !v)}
        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium transition-opacity hover:opacity-80"
        style={{
          backgroundColor: overridden ? 'var(--accent-bg)' : 'var(--bg-tertiary, var(--bg-primary))',
          color: overridden ? 'var(--accent)' : 'var(--text-secondary)',
          border: '1px solid var(--border-color)',
        }}
        title={tooltip}
        disabled={busy}
      >
        <Users size={10} style={{ flexShrink: 0 }} />
        <span>
          {roleDisplay(participant)}: {participant.displayName ?? participant.email ?? '—'}
        </span>
        {overridden && <span style={{ color: 'var(--accent)' }}>•</span>}
      </button>
      {menuOpen && !editing && (
        <div
          className="absolute z-10 mt-1 top-full left-0 rounded-lg border shadow-lg"
          style={{ backgroundColor: 'var(--bg-secondary)', borderColor: 'var(--border-color)' }}
        >
          <button
            type="button"
            onClick={() => { setMenuOpen(false); onReassign(); }}
            className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
            style={{ color: 'var(--text-primary)' }}
          >
            Reassign…
          </button>
          <button
            type="button"
            onClick={() => { setMenuOpen(false); onClear(); }}
            className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
            style={{ color: 'var(--danger)' }}
          >
            Clear (tombstone)
          </button>
        </div>
      )}
      {editing && (
        <InlineUserPicker
          role={participant.role}
          onPick={(picked) => onPick({ email: picked.email, displayName: picked.displayName })}
          onCancel={onCancelEdit}
          busy={busy}
        />
      )}
    </span>
  );
}

/**
 * Inline user picker — debounced search against /promotions/users/search. Anchored absolutely to
 * its parent (which must be `position: relative`); pops out below with a fixed width so the anchor
 * itself stays narrow.
 *
 * Two modes via the `role` prop:
 *   - role = string  → reassigning a known role; only the person is selected.
 *   - role = null    → new assignment; the operator types/picks the role too. Suggested roles come
 *                      from /api/promotions/roles via a `<datalist>`.
 *
 * Pass `onRemove` to offer clearing the slot from inside the popover — the row layout uses this
 * instead of a separate destructive button beside the person.
 *
 * Falls back to manual email entry when the directory returns no hits (local-auth dev).
 */
export function InlineUserPicker({
  role,
  onPick,
  onCancel,
  onRemove,
  removeLabel = 'Remove',
  busy,
  align = 'left',
}: {
  role: string | null;
  onPick: (picked: { role: string; email: string; displayName: string }) => void;
  onCancel: () => void;
  /** When supplied, the popover offers clearing this assignment. */
  onRemove?: () => void;
  removeLabel?: string;
  busy: boolean;
  /** Which edge the popover hangs from — `right` keeps it on screen when the anchor is right-aligned. */
  align?: 'left' | 'right';
}) {
  const roleEditable = role === null;
  const [roleInput, setRoleInput] = useState('');
  const [knownRoles, setKnownRoles] = useState<string[]>([]);
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string; email: string }>>([]);
  const [searching, setSearching] = useState(false);
  const datalistId = useMemo(() => `assign-roles-${Math.random().toString(36).slice(2, 8)}`, []);

  // Pre-fetch role suggestions when in role-editable mode.
  useEffect(() => {
    if (!roleEditable) return;
    let cancelled = false;
    api
      .listPromotionRoles()
      .then((d) => { if (!cancelled) setKnownRoles(d.roles || []); })
      .catch(() => { if (!cancelled) setKnownRoles([]); });
    return () => { cancelled = true; };
  }, [roleEditable]);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) { setResults([]); return; }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        if (!cancelled) setResults(res.users);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [query]);

  // Resolve the role to send: either the locked prop or whatever the operator typed.
  const effectiveRole = (role ?? roleInput).trim();
  const canSubmit = effectiveRole.length > 0;

  const submitWithUser = (u: { email: string; displayName: string }) => {
    if (!canSubmit) return;
    onPick({ role: effectiveRole, email: u.email, displayName: u.displayName });
  };

  const submitManual = () => {
    const q = query.trim();
    // Cheap email-shape check. Server validates again with the same rule.
    if (!q.includes('@') || !q.includes('.')) return;
    submitWithUser({ email: q, displayName: q });
  };

  return (
    <div
      className={`absolute z-20 mt-1 top-full ${align === 'right' ? 'right-0' : 'left-0'} rounded-lg border shadow-lg p-2 w-72`}
      style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
    >
      <div className="text-[11px] mb-1.5 px-1" style={{ color: 'var(--text-muted)' }}>
        {roleEditable ? 'Assign person' : `Assign ${roleDisplay({ role: role! })}`}
      </div>
      {roleEditable && (
        <>
          <input
            autoFocus
            list={datalistId}
            value={roleInput}
            onChange={(e) => setRoleInput(e.target.value)}
            placeholder="Role (e.g. QA, reviewer)"
            className="w-full rounded-lg border px-3 py-1.5 text-[13px] outline-none mb-1.5"
            style={{
              borderColor: 'var(--border-color)',
              backgroundColor: 'var(--bg-secondary)',
              color: 'var(--text-primary)',
            }}
            disabled={busy}
            onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); }}
          />
          <datalist id={datalistId}>
            {knownRoles.map((r) => (
              <option key={r} value={roleDisplay({ role: r })} />
            ))}
          </datalist>
        </>
      )}
      <input
        autoFocus={!roleEditable}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Search directory (name or email)..."
        className="w-full rounded-lg border px-3 py-1.5 text-[13px] outline-none"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-secondary)',
          color: 'var(--text-primary)',
        }}
        disabled={busy}
        onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); if (e.key === 'Enter' && results.length === 0) submitManual(); }}
      />
      {query.trim().length >= 2 && (
        <div className="mt-1 max-h-48 overflow-y-auto rounded-lg border" style={{ borderColor: 'var(--border-color)' }}>
          {searching && (
            <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
              Searching...
            </div>
          )}
          {!searching && results.length === 0 && (
            <button
              type="button"
              onClick={submitManual}
              className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
              disabled={busy || !canSubmit}
              title={!canSubmit ? 'Pick a role first' : undefined}
            >
              <span className="font-medium">Use &ldquo;{query.trim()}&rdquo; as email</span>
              <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                No directory matches — sent as-is.
              </span>
            </button>
          )}
          {!searching && results.map((u) => (
            <button
              key={u.id}
              type="button"
              onClick={() => submitWithUser({ email: u.email, displayName: u.displayName })}
              className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
              disabled={busy || !canSubmit}
              title={!canSubmit ? 'Pick a role first' : undefined}
            >
              <span className="font-medium truncate">{u.displayName}</span>
              <span className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                {u.email}
              </span>
            </button>
          ))}
        </div>
      )}
      <div className="mt-2 flex items-center justify-between gap-2">
        {onRemove ? (
          <button
            type="button"
            onClick={onRemove}
            className="inline-flex items-center gap-1 px-2 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
            style={{ color: 'var(--danger)' }}
            disabled={busy}
          >
            <X size={11} /> {removeLabel}
          </button>
        ) : (
          <span />
        )}
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 rounded-lg text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
          disabled={busy}
        >
          Cancel
        </button>
      </div>
    </div>
  );
}
