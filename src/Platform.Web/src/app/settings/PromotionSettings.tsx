import { useState, useEffect, useRef } from 'react';
import { useAuthStore } from '@/stores/authStore';
import {
  api,
  type PromotionPolicy,
  type PromotionPolicyStep,
  type PromotionPolicyRequirement,
  type PromotionPolicyGroupRef,
  type UpsertPromotionPolicyPayload,
} from '@/lib/api';
import { Plus, Trash2, Check, Pencil, X } from 'lucide-react';

const emptyRequirement = (): PromotionPolicyRequirement => ({
  name: '',
  groups: [],
  users: [],
  minApprovers: 1,
});

const emptyStep = (): PromotionPolicyStep => ({
  name: '',
  requirements: [emptyRequirement()],
});

const emptyForm: UpsertPromotionPolicyPayload = {
  product: '',
  service: null,
  targetEnv: '',
  steps: [],
  gate: 'PromotionOnly',
  timeoutHours: 24,
  escalationGroup: null,
  requireAllWorkItemsApproved: false,
  autoApproveOnAllWorkItemsApproved: false,
  autoApproveWhenNoWorkItems: false,
};

const inputClass =
  'px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]';
const inputStyle = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
};

const labelClass = 'text-[11px] font-medium uppercase tracking-wider';
const labelStyle = { color: 'var(--text-muted)' };

/** Summarise a step tree for the policy table. */
function summarizeSteps(steps: PromotionPolicyStep[]): string {
  if (!steps || steps.length === 0) return 'auto-approve';
  return steps
    .map((s, i) => {
      const reqs = s.requirements
        .map((r) => {
          const approvers = [...r.groups.map((g) => g.name), ...r.users];
          const who = approvers.length > 0 ? approvers.join(', ') : '—';
          return `${who} (${r.minApprovers})`;
        })
        .join(' + ');
      const name = s.name?.trim() || `Step ${i + 1}`;
      return `${name}: ${reqs}`;
    })
    .join('  →  ');
}

/** Per-requirement validation errors keyed by `${stepIdx}:${reqIdx}`. */
function validateSteps(steps: PromotionPolicyStep[]): Record<string, string> {
  const errors: Record<string, string> = {};
  steps.forEach((step, si) => {
    step.requirements.forEach((req, ri) => {
      const key = `${si}:${ri}`;
      if (req.groups.length === 0 && req.users.length === 0) {
        errors[key] = 'Add at least one group or user.';
      } else if (req.minApprovers < 1) {
        errors[key] = 'Min approvers must be at least 1.';
      }
    });
  });
  return errors;
}

/** Removable chip row shared by the group/user pickers. `label(v)` resolves a display label. */
function ChipRow({
  values,
  label,
  onRemove,
}: {
  values: string[];
  label: (v: string) => string;
  onRemove: (v: string) => void;
}) {
  if (values.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1.5">
      {values.map((v) => (
        <span
          key={v}
          className="inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-full border"
          style={{
            borderColor: 'var(--border-color)',
            backgroundColor: 'var(--bg-secondary)',
            color: 'var(--text-primary)',
          }}
          title={label(v) !== v ? v : undefined}
        >
          {label(v)}
          <button
            type="button"
            onClick={() => onRemove(v)}
            className="hover:opacity-80 transition-colors"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={12} />
          </button>
        </span>
      ))}
    </div>
  );
}

/**
 * Typeahead picker for user emails. Debounced search against /promotions/users/search; selecting
 * a hit adds the user's *email* to `values` (that's what the gate matches on). Falls back to
 * manual entry of an unmatched email so local-dev / edge cases still work.
 */
function UserPicker({
  values,
  onChange,
}: {
  values: string[];
  onChange: (next: string[]) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<
    Array<{ id: string; displayName: string; email: string }>
  >([]);
  const [searching, setSearching] = useState(false);
  const [focused, setFocused] = useState(false);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionUsers(q);
        if (!cancelled) setResults(res.users || []);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  const add = (email: string) => {
    const v = email.trim();
    if (!v || values.includes(v)) return;
    onChange([...values, v]);
    setQuery('');
    setResults([]);
  };

  const remove = (v: string) => onChange(values.filter((x) => x !== v));

  const trimmed = query.trim();
  const manualOk = trimmed.includes('@') && trimmed.includes('.');

  return (
    <div className="space-y-1.5">
      <ChipRow values={values} label={(v) => v} onRemove={remove} />
      <div className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => {
            blurTimer.current = setTimeout(() => setFocused(false), 150);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              if (results.length === 0 && manualOk) add(trimmed);
            }
          }}
          placeholder="Search directory (name or email)..."
          className={`${inputClass} w-full`}
          style={inputStyle}
        />
        {focused && trimmed.length >= 2 && (
          <div
            className="absolute z-20 mt-1 top-full left-0 right-0 max-h-48 overflow-y-auto rounded-lg border shadow-lg"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
            onMouseDown={() => {
              if (blurTimer.current) clearTimeout(blurTimer.current);
            }}
          >
            {searching && (
              <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Searching...
              </div>
            )}
            {!searching && results.length === 0 && (
              <button
                type="button"
                onClick={() => add(trimmed)}
                disabled={!manualOk}
                className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80 disabled:opacity-40"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium">Use &ldquo;{trimmed}&rdquo; as email</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {manualOk ? 'No directory matches — added as-is.' : 'No directory matches.'}
                </span>
              </button>
            )}
            {!searching &&
              results.map((u) => (
                <button
                  key={u.id}
                  type="button"
                  onClick={() => add(u.email)}
                  className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-primary)' }}
                >
                  <span className="font-medium truncate">{u.displayName}</span>
                  <span className="text-[11px] truncate" style={{ color: 'var(--text-muted)' }}>
                    {u.email}
                  </span>
                </button>
              ))}
          </div>
        )}
      </div>
    </div>
  );
}

/**
 * Typeahead picker for AD groups. Debounced search against /promotions/groups/search; selecting a
 * hit stores both the group's object *id* (the approval-time Graph lookup keys off the id) and its
 * display *name* as `{ id, name }`. The chip label shows the name, so a saved policy reloads showing
 * group names rather than raw object GUIDs. Unmatched manual entries are stored as `{id, name}` with
 * the typed text used for both. Falls back to manual entry too.
 */
function GroupPicker({
  values,
  onChange,
}: {
  values: PromotionPolicyGroupRef[];
  onChange: (next: PromotionPolicyGroupRef[]) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Array<{ id: string; displayName: string }>>([]);
  const [searching, setSearching] = useState(false);
  const [focused, setFocused] = useState(false);
  const blurTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    const timer = setTimeout(async () => {
      try {
        const res = await api.searchPromotionGroups(q);
        if (!cancelled) setResults(res.groups || []);
      } catch {
        if (!cancelled) setResults([]);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  const add = (id: string, displayName?: string) => {
    const trimmedId = id.trim();
    if (!trimmedId || values.some((g) => g.id === trimmedId)) return;
    onChange([...values, { id: trimmedId, name: (displayName ?? trimmedId).trim() }]);
    setQuery('');
    setResults([]);
  };

  const remove = (id: string) => onChange(values.filter((g) => g.id !== id));

  const trimmed = query.trim();

  return (
    <div className="space-y-1.5">
      <ChipRow values={values.map((g) => g.id)} label={(id) => values.find((g) => g.id === id)?.name ?? id} onRemove={remove} />
      <div className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => setFocused(true)}
          onBlur={() => {
            blurTimer.current = setTimeout(() => setFocused(false), 150);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              if (results.length === 0 && trimmed) add(trimmed);
            }
          }}
          placeholder="Search groups..."
          className={`${inputClass} w-full`}
          style={inputStyle}
        />
        {focused && trimmed.length >= 2 && (
          <div
            className="absolute z-20 mt-1 top-full left-0 right-0 max-h-48 overflow-y-auto rounded-lg border shadow-lg"
            style={{ backgroundColor: 'var(--bg-primary)', borderColor: 'var(--border-color)' }}
            onMouseDown={() => {
              if (blurTimer.current) clearTimeout(blurTimer.current);
            }}
          >
            {searching && (
              <div className="px-3 py-2 text-[12px]" style={{ color: 'var(--text-muted)' }}>
                Searching...
              </div>
            )}
            {!searching && results.length === 0 && (
              <button
                type="button"
                onClick={() => add(trimmed)}
                className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                style={{ color: 'var(--text-primary)' }}
              >
                <span className="font-medium">Use &ldquo;{trimmed}&rdquo; as group</span>
                <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  No directory matches — added as-is.
                </span>
              </button>
            )}
            {!searching &&
              results.map((g) => (
                <button
                  key={g.id}
                  type="button"
                  onClick={() => add(g.id, g.displayName)}
                  className="w-full text-left px-3 py-2 text-[13px] flex flex-col transition-opacity hover:opacity-80"
                  style={{ color: 'var(--text-primary)' }}
                >
                  <span className="font-medium truncate">{g.displayName}</span>
                </button>
              ))}
          </div>
        )}
      </div>
    </div>
  );
}

export function PromotionSettings() {
  const isAdmin = useAuthStore((s) => s.user?.isAdmin) ?? false;

  // ── Policies state ──
  const [policies, setPolicies] = useState<PromotionPolicy[]>([]);
  const [polLoading, setPolLoading] = useState(true);
  const [polError, setPolError] = useState<string | null>(null);
  const [polSaved, setPolSaved] = useState(false);

  // ── Form state (inline add/edit) ──
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<UpsertPromotionPolicyPayload>(emptyForm);
  const [formSaving, setFormSaving] = useState(false);
  const [stepErrors, setStepErrors] = useState<Record<string, string>>({});

  // ── Delete confirm ──
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  // ── Load data ──
  useEffect(() => {
    if (!isAdmin) return;
    api
      .listPromotionPolicies()
      .then((d) => setPolicies(d.policies))
      .catch(() => setPolError('Failed to load policies'))
      .finally(() => setPolLoading(false));
  }, [isAdmin]);

  if (!isAdmin) return null;

  // ── Policy handlers ──

  const openAddForm = () => {
    setForm(emptyForm);
    setStepErrors({});
    setEditingId(null);
    setShowForm(true);
  };

  const openEditForm = (p: PromotionPolicy) => {
    setForm({
      product: p.product,
      service: p.service,
      targetEnv: p.targetEnv,
      // Deep clone so edits don't mutate the list row.
      steps: p.steps.map((s) => ({
        name: s.name,
        requirements: s.requirements.map((r) => ({
          name: r.name,
          groups: [...r.groups],
          users: [...r.users],
          minApprovers: r.minApprovers,
        })),
      })),
      gate: p.gate ?? 'PromotionOnly',
      timeoutHours: p.timeoutHours,
      escalationGroup: p.escalationGroup,
      requireAllWorkItemsApproved: p.requireAllWorkItemsApproved ?? false,
      autoApproveOnAllWorkItemsApproved: p.autoApproveOnAllWorkItemsApproved ?? false,
      autoApproveWhenNoWorkItems: p.autoApproveWhenNoWorkItems ?? false,
    });
    setStepErrors({});
    setEditingId(p.id);
    setShowForm(true);
  };

  const cancelForm = () => {
    setShowForm(false);
    setEditingId(null);
    setForm(emptyForm);
    setStepErrors({});
  };

  const handleSavePolicy = async () => {
    const errors = validateSteps(form.steps);
    if (Object.keys(errors).length > 0) {
      setStepErrors(errors);
      return;
    }
    setStepErrors({});
    setFormSaving(true);
    setPolError(null);
    setPolSaved(false);
    try {
      const result = await api.upsertPromotionPolicy(form, editingId ?? undefined);
      if (editingId) {
        setPolicies((prev) => prev.map((p) => (p.id === editingId ? result : p)));
      } else {
        setPolicies((prev) => [...prev, result]);
      }
      cancelForm();
      setPolSaved(true);
      setTimeout(() => setPolSaved(false), 2000);
    } catch (e) {
      setPolError(e instanceof Error ? e.message : 'Failed to save policy');
    } finally {
      setFormSaving(false);
    }
  };

  const handleDeletePolicy = async (id: string) => {
    if (deleteConfirm !== id) {
      setDeleteConfirm(id);
      return;
    }
    setDeleteConfirm(null);
    setPolError(null);
    try {
      await api.deletePromotionPolicy(id);
      setPolicies((prev) => prev.filter((p) => p.id !== id));
    } catch (e) {
      setPolError(e instanceof Error ? e.message : 'Failed to delete policy');
    }
  };

  const setField = <K extends keyof UpsertPromotionPolicyPayload>(
    key: K,
    value: UpsertPromotionPolicyPayload[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  // ── Step / requirement mutators ──

  const addStep = () => setForm((p) => ({ ...p, steps: [...p.steps, emptyStep()] }));

  const removeStep = (si: number) =>
    setForm((p) => ({ ...p, steps: p.steps.filter((_, i) => i !== si) }));

  const updateStepName = (si: number, name: string) =>
    setForm((p) => ({
      ...p,
      steps: p.steps.map((s, i) => (i === si ? { ...s, name } : s)),
    }));

  const addRequirement = (si: number) =>
    setForm((p) => ({
      ...p,
      steps: p.steps.map((s, i) =>
        i === si ? { ...s, requirements: [...s.requirements, emptyRequirement()] } : s,
      ),
    }));

  const removeRequirement = (si: number, ri: number) =>
    setForm((p) => ({
      ...p,
      steps: p.steps.map((s, i) =>
        i === si ? { ...s, requirements: s.requirements.filter((_, j) => j !== ri) } : s,
      ),
    }));

  const updateRequirement = (
    si: number,
    ri: number,
    patch: Partial<PromotionPolicyRequirement>,
  ) =>
    setForm((p) => ({
      ...p,
      steps: p.steps.map((s, i) =>
        i === si
          ? {
              ...s,
              requirements: s.requirements.map((r, j) => (j === ri ? { ...r, ...patch } : r)),
            }
          : s,
      ),
    }));

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Promotions
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Manage promotion approval policies.
        </p>
      </div>

      {/* ══════════ Promotion Policies ══════════ */}
      <div className="space-y-3">
        <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Promotion Policies
        </h3>

        {polLoading ? (
          <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            Loading policies…
          </p>
        ) : (
          <>
            {/* Table */}
            {policies.length > 0 && (
              <div className="overflow-x-auto">
                <table className="w-full text-[13px]" style={{ color: 'var(--text-primary)' }}>
                  <thead>
                    <tr
                      className="text-left text-[11px] font-medium uppercase tracking-wider"
                      style={{ color: 'var(--text-muted)' }}
                    >
                      <th className="pb-2 pr-3">Product</th>
                      <th className="pb-2 pr-3">Service</th>
                      <th className="pb-2 pr-3">Target Env</th>
                      <th className="pb-2 pr-3">Approval Steps</th>
                      <th className="pb-2 pr-3">Gate</th>
                      <th className="pb-2">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {policies.map((p) => (
                      <tr
                        key={p.id}
                        className="border-t"
                        style={{ borderColor: 'var(--border-color)' }}
                      >
                        <td className="py-2 pr-3">{p.product}</td>
                        <td
                          className="py-2 pr-3"
                          style={{ color: p.service ? undefined : 'var(--text-muted)' }}
                        >
                          {p.service || '—'}
                        </td>
                        <td className="py-2 pr-3">{p.targetEnv}</td>
                        <td
                          className="py-2 pr-3"
                          style={{
                            color: p.steps?.length ? undefined : 'var(--text-muted)',
                          }}
                        >
                          {summarizeSteps(p.steps)}
                        </td>
                        <td className="py-2 pr-3">{p.gate}</td>
                        <td className="py-2">
                          <div className="flex items-center gap-1.5">
                            <button
                              onClick={() => openEditForm(p)}
                              className="p-1 rounded-lg transition-colors hover:opacity-80"
                              style={{ color: 'var(--text-muted)' }}
                            >
                              <Pencil size={14} />
                            </button>
                            <button
                              onClick={() => handleDeletePolicy(p.id)}
                              className="p-1 rounded-lg transition-colors hover:opacity-80"
                              style={{
                                color:
                                  deleteConfirm === p.id
                                    ? 'var(--danger, #dc2626)'
                                    : 'var(--text-muted)',
                              }}
                            >
                              <Trash2 size={14} />
                              {deleteConfirm === p.id && (
                                <span className="text-[11px] ml-1">Click again to confirm</span>
                              )}
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {policies.length === 0 && (
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                No promotion policies defined.
              </p>
            )}

            {/* Add Policy button */}
            {!showForm && (
              <button
                onClick={openAddForm}
                className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
                style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
              >
                <Plus size={14} />
                Add Policy
              </button>
            )}

            {/* Inline form */}
            {showForm && (
              <div
                className="rounded-lg border p-4 space-y-3"
                style={{
                  borderColor: 'var(--border-color)',
                  backgroundColor: 'var(--bg-primary)',
                }}
              >
                <h4 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                  {editingId ? 'Edit Policy' : 'New Policy'}
                </h4>

                <div className="grid grid-cols-2 gap-3">
                  {/* Product */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Product *
                    </label>
                    <input
                      type="text"
                      value={form.product}
                      onChange={(e) => setField('product', e.target.value)}
                      placeholder="e.g. my-product"
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    />
                  </div>

                  {/* Service */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Service
                    </label>
                    <input
                      type="text"
                      value={form.service ?? ''}
                      onChange={(e) => setField('service', e.target.value || null)}
                      placeholder="empty = product-default"
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    />
                  </div>

                  {/* Target Env */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Target Env *
                    </label>
                    <input
                      type="text"
                      value={form.targetEnv}
                      onChange={(e) => setField('targetEnv', e.target.value)}
                      placeholder="e.g. production"
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    />
                  </div>

                  {/* Timeout Hours */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Timeout Hours
                    </label>
                    <input
                      type="number"
                      min={1}
                      value={form.timeoutHours}
                      onChange={(e) => setField('timeoutHours', Number(e.target.value))}
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    />
                  </div>

                  {/* Escalation Group */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Escalation Group
                    </label>
                    <input
                      type="text"
                      value={form.escalationGroup ?? ''}
                      onChange={(e) => setField('escalationGroup', e.target.value || null)}
                      placeholder="optional"
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    />
                  </div>

                  {/* Approval Gate */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Approval Gate
                    </label>
                    <select
                      value={form.gate}
                      onChange={(e) =>
                        setField('gate', e.target.value as UpsertPromotionPolicyPayload['gate'])
                      }
                      className={`${inputClass} w-full`}
                      style={inputStyle}
                    >
                      <option value="PromotionOnly">Promotion only (manual)</option>
                      <option value="WorkItemsOnly">Work items only (auto when all approved)</option>
                      <option value="WorkItemsAndManual">Work items + manual</option>
                    </select>
                  </div>
                </div>

                {/* ── Approval steps ── */}
                <div
                  className="rounded-lg border p-3 space-y-3"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-secondary)',
                  }}
                >
                  <div className="flex items-center justify-between">
                    <p
                      className="text-[11px] font-semibold uppercase tracking-wider"
                      style={{ color: 'var(--text-muted)' }}
                    >
                      Approval steps
                    </p>
                    <button
                      type="button"
                      onClick={addStep}
                      className="inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-lg transition-colors hover:opacity-80"
                      style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
                    >
                      <Plus size={13} />
                      Add Step
                    </button>
                  </div>

                  {form.steps.length === 0 && (
                    <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                      No steps — promotions to this target auto-approve.
                    </p>
                  )}

                  {form.steps.map((step, si) => (
                    <div
                      key={si}
                      className="rounded-lg border p-3 space-y-3"
                      style={{
                        borderColor: 'var(--border-color)',
                        backgroundColor: 'var(--bg-primary)',
                      }}
                    >
                      <div className="flex items-center gap-2">
                        <span
                          className="text-[11px] font-semibold"
                          style={{ color: 'var(--text-muted)' }}
                        >
                          Step {si + 1}
                        </span>
                        <input
                          type="text"
                          value={step.name}
                          onChange={(e) => updateStepName(si, e.target.value)}
                          placeholder="Step name (e.g. Security review)"
                          className={`${inputClass} flex-1`}
                          style={inputStyle}
                        />
                        <button
                          type="button"
                          onClick={() => removeStep(si)}
                          className="p-1 rounded-lg transition-colors hover:opacity-80"
                          style={{ color: 'var(--text-muted)' }}
                          title="Remove step"
                        >
                          <Trash2 size={14} />
                        </button>
                      </div>

                      {/* Requirements */}
                      <div className="space-y-2 pl-2 border-l-2" style={{ borderColor: 'var(--border-color)' }}>
                        {step.requirements.map((req, ri) => {
                          const errKey = `${si}:${ri}`;
                          const err = stepErrors[errKey];
                          return (
                            <div
                              key={ri}
                              className="rounded-lg border p-3 space-y-2.5"
                              style={{
                                borderColor: err
                                  ? 'var(--danger, #dc2626)'
                                  : 'var(--border-color)',
                                backgroundColor: 'var(--bg-secondary)',
                              }}
                            >
                              <div className="flex items-center gap-2">
                                <input
                                  type="text"
                                  value={req.name}
                                  onChange={(e) =>
                                    updateRequirement(si, ri, { name: e.target.value })
                                  }
                                  placeholder="Requirement name (optional)"
                                  className={`${inputClass} flex-1`}
                                  style={inputStyle}
                                />
                                <button
                                  type="button"
                                  onClick={() => removeRequirement(si, ri)}
                                  className="p-1 rounded-lg transition-colors hover:opacity-80"
                                  style={{ color: 'var(--text-muted)' }}
                                  title="Remove requirement"
                                >
                                  <X size={14} />
                                </button>
                              </div>

                              <div className="grid grid-cols-2 gap-3">
                                <div className="space-y-1">
                                  <label className={labelClass} style={labelStyle}>
                                    AD Groups
                                  </label>
                                  <GroupPicker
                                    values={req.groups}
                                    onChange={(groups) =>
                                      updateRequirement(si, ri, { groups })
                                    }
                                  />
                                </div>
                                <div className="space-y-1">
                                  <label className={labelClass} style={labelStyle}>
                                    User Emails
                                  </label>
                                  <UserPicker
                                    values={req.users}
                                    onChange={(users) => updateRequirement(si, ri, { users })}
                                  />
                                </div>
                              </div>

                              <div className="space-y-1 w-40">
                                <label className={labelClass} style={labelStyle}>
                                  Min Approvers
                                </label>
                                <input
                                  type="number"
                                  min={1}
                                  value={req.minApprovers}
                                  onChange={(e) =>
                                    updateRequirement(si, ri, {
                                      minApprovers: Number(e.target.value),
                                    })
                                  }
                                  className={`${inputClass} w-full`}
                                  style={inputStyle}
                                />
                              </div>

                              {err && (
                                <p
                                  className="text-[12px]"
                                  style={{ color: 'var(--danger, #dc2626)' }}
                                >
                                  {err}
                                </p>
                              )}
                            </div>
                          );
                        })}

                        <button
                          type="button"
                          onClick={() => addRequirement(si)}
                          className="inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-lg transition-colors hover:opacity-80"
                          style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
                        >
                          <Plus size={13} />
                          Add Requirement
                        </button>
                      </div>
                    </div>
                  ))}
                </div>

                {/* ── Work-item-gate options ── */}
                <div
                  className="rounded-lg border p-3 space-y-2"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-secondary)',
                  }}
                >
                  <p
                    className="text-[11px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    Work-item-gate options
                  </p>

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.requireAllWorkItemsApproved}
                      onChange={(e) => setField('requireAllWorkItemsApproved', e.target.checked)}
                      className="mt-0.5 rounded"
                    />
                    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
                      All work items must be approved before promotion can be approved
                      <span
                        className="block text-[11px] mt-0.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        Blocks the Approve button until every work item has a sign-off.
                      </span>
                    </span>
                  </label>

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.autoApproveOnAllWorkItemsApproved}
                      onChange={(e) =>
                        setField('autoApproveOnAllWorkItemsApproved', e.target.checked)
                      }
                      className="mt-0.5 rounded"
                    />
                    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
                      Auto-approve promotion when all work items are approved
                      <span
                        className="block text-[11px] mt-0.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        Promotion is automatically approved the moment the last work item gets its
                        sign-off.
                      </span>
                    </span>
                  </label>

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.autoApproveWhenNoWorkItems}
                      onChange={(e) => setField('autoApproveWhenNoWorkItems', e.target.checked)}
                      className="mt-0.5 rounded"
                    />
                    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
                      Auto-approve promotion when no work items are assigned
                      <span
                        className="block text-[11px] mt-0.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        If the deploy event has no work-item references, skip the approval gate
                        entirely.
                      </span>
                    </span>
                  </label>
                </div>

                {/* Form actions */}
                <div className="flex items-center gap-2 pt-1">
                  <button
                    onClick={handleSavePolicy}
                    disabled={formSaving || !form.product.trim() || !form.targetEnv.trim()}
                    className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
                    style={{ backgroundColor: 'var(--accent)' }}
                  >
                    {formSaving ? 'Saving…' : editingId ? 'Update Policy' : 'Save Policy'}
                  </button>
                  <button
                    onClick={cancelForm}
                    disabled={formSaving}
                    className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg transition-colors hover:opacity-80"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}

            {polSaved && (
              <span
                className="inline-flex items-center gap-1 text-[13px]"
                style={{ color: 'var(--success)' }}
              >
                <Check size={14} /> Saved
              </span>
            )}

            {polError && (
              <div
                className="text-[13px] rounded-lg px-3 py-2"
                style={{
                  color: 'var(--danger, #dc2626)',
                  backgroundColor: 'var(--danger-muted, #fee2e2)',
                }}
              >
                {polError}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
