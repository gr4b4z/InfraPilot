import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import type { PromotionSourceEventParticipant } from '@/lib/api';
import { roleDisplay, useConfiguredRoles, useIsUnrecognisedRole } from '@/lib/roleLabel';
import { formatReferenceParticipant } from '@/lib/workItem';
import { CopyEmailButton } from '@/components/deployments/CopyEmailButton';
import { AnchoredPopover } from '@/components/ui/AnchoredPopover';
import { RovingGroup } from '@/components/ui/RovingGroup';
import { ROW_ACTION_ATTR } from '@/lib/keys';
import { AlertTriangle, Plus, Users, X } from 'lucide-react';

/**
 * The people assigned to one work-item reference of a promotion candidate, with assign / reassign /
 * remove controls. Writes go to
 * `PATCH /api/promotions/{candidateId}/references/{key}/participants` — candidates are
 * self-contained (there is no deploy event to override), so the candidate id is the write target.
 *
 * Shared by the promotion detail page's work-item rows and the work-item detail page so the two
 * surfaces can't drift on what assignment means or which endpoint it hits.
 *
 * Roles arrive from ingest exactly as the producer sent them, so a work item can carry one that
 * isn't in the configured vocabulary (Settings → Participant Roles). Those are flagged wherever
 * they render and can only be cleared, not reassigned: the server refuses to name a person on an
 * unconfigured role, and a slot the platform can't label or route on is a data problem to fix at
 * the source, not a slot to keep filling.
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
  const isUnrecognised = useIsUnrecognisedRole();
  // One per layout, since only one of the two "Assign" buttons is ever rendered.
  const rowsAssignRef = useRef<HTMLButtonElement>(null);
  const chipsAssignRef = useRef<HTMLButtonElement>(null);

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
        {participants.map((p, i) => {
          const unrecognised = isUnrecognised(p.role);
          return (
            <span
              key={`${p.role}-${i}`}
              className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium"
              style={{
                backgroundColor: unrecognised
                  ? 'var(--warning-bg)'
                  : 'var(--bg-tertiary, var(--bg-primary))',
                color: unrecognised ? 'var(--warning)' : 'var(--text-secondary)',
                border: `1px solid ${unrecognised ? 'var(--warning)' : 'var(--border-color)'}`,
              }}
              title={
                unrecognised
                  ? `${formatReferenceParticipant(p)} — "${p.role}" is not a configured participant role`
                  : formatReferenceParticipant(p)
              }
            >
              {unrecognised ? (
                <AlertTriangle size={10} style={{ flexShrink: 0 }} />
              ) : (
                <Users size={10} style={{ flexShrink: 0 }} />
              )}
              <span>
                {roleDisplay(p)}: {p.displayName ?? p.email ?? '—'}
              </span>
            </span>
          );
        })}
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
            ref={rowsAssignRef}
            type="button"
            {...{ [ROW_ACTION_ATTR]: 'assign' }}
            onClick={() => setEditingRole(newAssignOpen ? null : '')}
            className="inline-flex items-center gap-1 px-2 py-1 rounded-lg text-[11px] font-medium transition-opacity hover:opacity-80"
            style={{ border: '1px dashed var(--border-color)', color: 'var(--text-muted)' }}
            disabled={busy}
          >
            <Plus size={11} /> Assign person
          </button>
          {newAssignOpen && (
            <InlineUserPicker
              anchorRef={rowsAssignRef}
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
    // One tab stop for the whole row of people, with the side arrows moving between them. Arrowing
    // must not activate: each chip opens an assign popover, so focus-follows-selection would open and
    // close a picker for every person you passed.
    <RovingGroup
      ariaLabel="Assigned people"
      className="mt-1 flex flex-wrap items-center gap-1.5"
      activateOnArrow={false}
    >
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
          ref={chipsAssignRef}
          type="button"
          {...{ [ROW_ACTION_ATTR]: 'assign' }}
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
            anchorRef={chipsAssignRef}
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
    </RovingGroup>
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
  const unrecognised = useIsUnrecognisedRole()(participant.role);
  const rowRef = useRef<HTMLDivElement>(null);

  const body = (
    <>
      <div
        className="text-[11px] inline-flex items-center gap-1"
        style={{ color: unrecognised ? 'var(--warning)' : 'var(--text-muted)' }}
      >
        {unrecognised && (
          <span title={`"${participant.role}" is not a configured participant role`}>
            <AlertTriangle size={10} />
          </span>
        )}
        {roleDisplay(participant)}
        {unrecognised && <span> · not configured</span>}
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
        ref={rowRef}
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
        title={
          unrecognised
            ? `"${participant.role}" is not a configured participant role — this slot can only be removed`
            : `Reassign or remove the ${roleDisplay(participant)}`
        }
        aria-label={`Edit ${roleDisplay(participant)} ${label}`}
      >
        {body}
      </div>
      {editing && onPick && onCancelEdit && (
        <InlineUserPicker
          anchorRef={rowRef}
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
  const chipRef = useRef<HTMLButtonElement>(null);
  const overridden = participant.isOverride === true;
  const unrecognised = useIsUnrecognisedRole()(participant.role);
  const baseTooltip = overridden && participant.assignedBy
    ? `${formatReferenceParticipant(participant)} (overridden by ${participant.assignedBy})`
    : formatReferenceParticipant(participant);
  const tooltip = unrecognised
    ? `${baseTooltip} — "${participant.role}" is not a configured participant role`
    : baseTooltip;

  return (
    <span className="inline-flex items-center relative">
      <button
        ref={chipRef}
        type="button"
        onClick={() => setMenuOpen((v) => !v)}
        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium transition-opacity hover:opacity-80"
        style={{
          backgroundColor: unrecognised
            ? 'var(--warning-bg)'
            : overridden
              ? 'var(--accent-bg)'
              : 'var(--bg-tertiary, var(--bg-primary))',
          color: unrecognised
            ? 'var(--warning)'
            : overridden
              ? 'var(--accent)'
              : 'var(--text-secondary)',
          border: `1px solid ${unrecognised ? 'var(--warning)' : 'var(--border-color)'}`,
        }}
        title={tooltip}
        disabled={busy}
      >
        {unrecognised ? (
          <AlertTriangle size={10} style={{ flexShrink: 0 }} />
        ) : (
          <Users size={10} style={{ flexShrink: 0 }} />
        )}
        <span>
          {roleDisplay(participant)}: {participant.displayName ?? participant.email ?? '—'}
        </span>
        {overridden && !unrecognised && <span style={{ color: 'var(--accent)' }}>•</span>}
      </button>
      {menuOpen && !editing && (
        <AnchoredPopover anchorRef={chipRef} onClose={() => setMenuOpen(false)}>
          {/* Reassigning an unconfigured role is refused server-side, so the menu says why instead
              of offering a control that can only fail. Clearing stays available — that's the fix. */}
          {unrecognised ? (
            <p
              className="px-3 py-1.5 text-[11px] max-w-[15rem]"
              style={{ color: 'var(--text-muted)' }}
            >
              <span style={{ color: 'var(--warning)' }}>“{participant.role}” isn't a configured role.</span>{' '}
              Add it under Settings → Participant Roles to assign people to it.
            </p>
          ) : (
            <button
              type="button"
              onClick={() => { setMenuOpen(false); onReassign(); }}
              className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
              style={{ color: 'var(--text-primary)' }}
            >
              Reassign…
            </button>
          )}
          <button
            type="button"
            onClick={() => { setMenuOpen(false); onClear(); }}
            className="block w-full text-left px-3 py-1.5 text-[11px] hover:opacity-80"
            style={{ color: 'var(--danger)' }}
          >
            Clear (tombstone)
          </button>
        </AnchoredPopover>
      )}
      {editing && (
        <InlineUserPicker
          anchorRef={chipRef}
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
 * Inline user picker — debounced search against /promotions/users/search. Pops out below its anchor
 * with a fixed width so the anchor itself stays narrow.
 *
 * Rendered through {@link AnchoredPopover}, i.e. into `document.body`, because these chips live
 * inside `.card-hover` list cards: the hover transform makes each card a stacking context, which used
 * to leave the picker painted underneath the following card.
 *
 * Two modes via the `role` prop:
 *   - role = string  → reassigning that role; only the person is selected. When the role isn't in
 *                      the configured vocabulary the popover explains and offers no assignment —
 *                      the server refuses it, and a role nothing can label or filter on isn't a
 *                      slot to keep filling.
 *   - role = null    → new assignment; the operator picks the role from the configured list. A
 *                      free-text role would let one typo create a permanently unroutable slot, so
 *                      the vocabulary is the whole menu.
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
  anchorRef,
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
  /** The control the picker hangs off. */
  anchorRef: React.RefObject<HTMLElement | null>;
}) {
  const roleEditable = role === null;
  const [roleInput, setRoleInput] = useState('');
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string; email: string }>>([]);
  const [searching, setSearching] = useState(false);
  // The assignable vocabulary — the same list the server validates against, so the popover can't
  // offer a role the write would reject.
  const configuredRoles = useConfiguredRoles();
  const isUnrecognised = useIsUnrecognisedRole();
  const lockedRoleUnrecognised = !roleEditable && isUnrecognised(role);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2 || lockedRoleUnrecognised) { setResults([]); return; }
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
  }, [query, lockedRoleUnrecognised]);

  // Resolve the role to send: either the locked prop or the one picked from the list.
  const effectiveRole = (role ?? roleInput).trim();
  const canSubmit = effectiveRole.length > 0 && !lockedRoleUnrecognised;

  // Which result the arrow keys are on. -1 means "none yet", so the first ArrowDown lands on the
  // first result rather than the second. Reset whenever the result set changes, since index 2 of
  // the previous search means nothing in the new one.
  const [highlighted, setHighlighted] = useState(-1);
  const resultsRef = useRef<HTMLDivElement>(null);
  useEffect(() => setHighlighted(-1), [results]);

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

  // Arrow keys drive the result list from the search box, so picking someone is type-then-Enter
  // rather than type-then-Tab-past-every-name. Handled on the input because that is where focus
  // stays while typing.
  const onSearchKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') { onCancel(); return; }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (results.length === 0) return;
      event.preventDefault();
      const delta = event.key === 'ArrowDown' ? 1 : -1;
      const next = Math.max(0, Math.min(highlighted + delta, results.length - 1));
      setHighlighted(next);
      // Keep the highlighted row inside the scrollable result box.
      resultsRef.current
        ?.querySelectorAll<HTMLElement>('[data-result-index]')[next]
        ?.scrollIntoView({ block: 'nearest' });
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      if (results.length === 0) { submitManual(); return; }
      // Enter with nothing arrowed to takes the top hit — the common case after typing a name.
      const pick = results[highlighted === -1 ? 0 : highlighted];
      if (pick) submitWithUser({ email: pick.email, displayName: pick.displayName });
    }
  };

  return (
    <AnchoredPopover
      anchorRef={anchorRef}
      onClose={onCancel}
      align={align}
      width={288}
      className="p-2"
      style={{ backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="text-[11px] mb-1.5 px-1" style={{ color: 'var(--text-muted)' }}>
        {roleEditable ? 'Assign person' : `Assign ${roleDisplay({ role: role! })}`}
      </div>
      {roleEditable && (
        <select
          autoFocus
          value={roleInput}
          onChange={(e) => setRoleInput(e.target.value)}
          className="w-full rounded-lg border px-3 py-1.5 text-[13px] outline-none mb-1.5"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-secondary)',
            color: 'var(--text-primary)',
          }}
          disabled={busy || configuredRoles.length === 0}
          onKeyDown={(e) => { if (e.key === 'Escape') onCancel(); }}
        >
          <option value="">
            {configuredRoles.length === 0 ? 'No roles configured' : 'Pick a role…'}
          </option>
          {configuredRoles.map((r) => (
            <option key={r.key} value={r.key}>
              {r.displayName}
            </option>
          ))}
        </select>
      )}
      {roleEditable && configuredRoles.length === 0 && (
        <p className="text-[11px] px-1 mb-1.5" style={{ color: 'var(--warning)' }}>
          Add participant roles under Settings → Participant Roles before assigning anyone.
        </p>
      )}
      {/* Locked onto a role nobody configured: the write would be refused, so the popover offers
          the explanation (and the Remove button, when the caller supplied one) instead of a search
          box that leads nowhere. */}
      {lockedRoleUnrecognised && (
        <p
          className="text-[11px] px-1 flex items-start gap-1"
          style={{ color: 'var(--text-muted)' }}
        >
          <AlertTriangle size={11} style={{ color: 'var(--warning)', flexShrink: 0, marginTop: 1 }} />
          <span>
            <span style={{ color: 'var(--warning)' }}>“{role}” isn't a configured participant role.</span>{' '}
            Add it under Settings → Participant Roles to assign someone, or clear the slot.
          </span>
        </p>
      )}
      {!lockedRoleUnrecognised && (
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
          role="combobox"
          aria-expanded={results.length > 0}
          aria-controls="assign-results"
          aria-activedescendant={highlighted >= 0 ? `assign-result-${highlighted}` : undefined}
          onKeyDown={onSearchKeyDown}
        />
      )}
      {!lockedRoleUnrecognised && query.trim().length >= 2 && (
        <div
          ref={resultsRef}
          id="assign-results"
          role="listbox"
          className="mt-1 max-h-48 overflow-y-auto rounded-lg border"
          style={{ borderColor: 'var(--border-color)' }}
        >
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
          {!searching && results.map((u, i) => (
            <button
              key={u.id}
              type="button"
              id={`assign-result-${i}`}
              data-result-index={i}
              role="option"
              aria-selected={highlighted === i}
              onMouseEnter={() => setHighlighted(i)}
              onClick={() => submitWithUser({ email: u.email, displayName: u.displayName })}
              className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
              style={{
                color: 'var(--text-primary)',
                // The arrowed-to row is tinted rather than focused: focus stays in the search box so
                // the user can keep typing to narrow, which is how a combobox is expected to behave.
                backgroundColor: highlighted === i ? 'var(--accent-muted)' : undefined,
              }}
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
    </AnchoredPopover>
  );
}
