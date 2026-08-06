import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { AlertTriangle, Plus, Trash2, Check, Pencil, X, ShieldAlert, Loader2 } from 'lucide-react';
import { useAuthStore } from '@/stores/authStore';
import {
  api,
  type RollbackPolicy,
  type UpsertRollbackPolicyPayload,
  type PromotionPolicyStep,
  type PromotionPolicyRequirement,
  type RollbackPrincipalSet,
} from '@/lib/api';
import { useFeatureFlag, FeatureFlag } from '@/stores/featureFlagsStore';
import { UserPicker, GroupPicker } from './approverPickers';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * Per-product rollback permissions: who may create a rollback and who must approve it. One policy per
 * (product, target environment), where an empty environment is the product default covering every
 * environment without its own row.
 *
 * <p>The existence of a policy is also the product's rollback enrollment — it replaced the old
 * enabled-products checkbox list, so there is no longer a separate "allowed" toggle to keep in sync
 * with the permissions that make rollbacks usable.</p>
 *
 * <p>Three states are worth calling out on screen because each looks configured but grants something
 * different: no policy (admins only, and every request needs an override), a policy with no creators
 * (admins only), and a policy with no approval steps (no gate at all).</p>
 */

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

const emptyCreators = (): RollbackPrincipalSet => ({ groups: [], users: [] });

const emptyForm: UpsertRollbackPolicyPayload = {
  product: '',
  targetEnv: null,
  creators: emptyCreators(),
  steps: [],
  escalationGroup: null,
};

/** Summarise a principal set for the table. */
function summarizePrincipals(set: RollbackPrincipalSet | undefined): string {
  const all = [...(set?.groups ?? []).map((g) => g.name), ...(set?.users ?? [])];
  return all.length > 0 ? all.join(', ') : 'admins only';
}

/** Summarise an approval tree for the table. */
function summarizeSteps(steps: PromotionPolicyStep[] | undefined): string {
  if (!steps || steps.length === 0) return 'no approval required';
  const reqs = steps.flatMap((s) => s.requirements ?? []);
  if (reqs.length === 0) return 'no approval required';
  return reqs
    .map((r) => {
      const who = [...r.groups.map((g) => g.name), ...r.users];
      return `${who.length > 0 ? who.join(', ') : '—'} (${r.minApprovers})`;
    })
    .join(' + ');
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

export function RollbackSettings() {
  const isAdmin = useAuthStore((s) => s.user?.isAdmin) ?? false;
  const flagOn = useFeatureFlag(FeatureFlag.Rollbacks);

  const [policies, setPolicies] = useState<RollbackPolicy[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<UpsertRollbackPolicyPayload>(emptyForm);
  const [formSaving, setFormSaving] = useState(false);
  const [stepErrors, setStepErrors] = useState<Record<string, string>>({});
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  useEffect(() => {
    if (!isAdmin) return;
    let cancelled = false;
    api
      .listRollbackPolicies()
      .then((d) => !cancelled && setPolicies(d.policies))
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : 'Failed to load policies'))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [isAdmin]);

  if (!isAdmin) return null;

  const openAddForm = () => {
    setForm(emptyForm);
    setStepErrors({});
    setEditingId(null);
    setShowForm(true);
  };

  const openEditForm = (p: RollbackPolicy) => {
    setForm({
      product: p.product,
      targetEnv: p.targetEnv,
      // Deep clone so edits don't mutate the list row.
      creators: {
        groups: [...(p.creators?.groups ?? [])],
        users: [...(p.creators?.users ?? [])],
      },
      steps: (p.steps ?? []).map((s) => ({
        name: s.name,
        requirements: s.requirements.map((r) => ({
          name: r.name,
          groups: [...r.groups],
          users: [...r.users],
          minApprovers: r.minApprovers,
        })),
      })),
      escalationGroup: p.escalationGroup,
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

  const handleSave = async () => {
    const errors = validateSteps(form.steps);
    if (Object.keys(errors).length > 0) {
      setStepErrors(errors);
      return;
    }
    setStepErrors({});
    setFormSaving(true);
    setError(null);
    setSaved(false);
    try {
      const result = await api.upsertRollbackPolicy(form, editingId ?? undefined);
      setPolicies((prev) =>
        editingId ? prev.map((p) => (p.id === editingId ? result : p)) : [...prev, result],
      );
      cancelForm();
      setSaved(true);
      setTimeout(() => setSaved(false), 3000);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save policy');
    } finally {
      setFormSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    if (deleteConfirm !== id) {
      setDeleteConfirm(id);
      return;
    }
    setDeleteConfirm(null);
    setError(null);
    try {
      await api.deleteRollbackPolicy(id);
      setPolicies((prev) => prev.filter((p) => p.id !== id));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to delete policy');
    }
  };

  const setField = <K extends keyof UpsertRollbackPolicyPayload>(
    key: K,
    value: UpsertRollbackPolicyPayload[K],
  ) => setForm((prev) => ({ ...prev, [key]: value }));

  // ── Step / requirement mutators ──

  const addStep = () => setForm((p) => ({ ...p, steps: [...p.steps, emptyStep()] }));

  const removeStep = (si: number) =>
    setForm((p) => ({ ...p, steps: p.steps.filter((_, i) => i !== si) }));

  const updateStepName = (si: number, name: string) =>
    setForm((p) => ({ ...p, steps: p.steps.map((s, i) => (i === si ? { ...s, name } : s)) }));

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

  const updateRequirement = (si: number, ri: number, patch: Partial<PromotionPolicyRequirement>) =>
    setForm((p) => ({
      ...p,
      steps: p.steps.map((s, i) =>
        i === si
          ? { ...s, requirements: s.requirements.map((r, j) => (j === ri ? { ...r, ...patch } : r)) }
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
          Rollbacks
        </h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Set who can create rollbacks for each product and who must approve them. A product with no
          policy here can only be rolled back by an admin, and every such request needs an explicit
          approval-gate override.
        </p>
      </div>

      {!flagOn && (
        <div
          className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--text-secondary)' }}
        >
          <AlertTriangle size={14} className="shrink-0 mt-0.5" />
          <span>
            The <strong>Rollbacks</strong> feature is globally off. Policies here have no effect until
            you enable it in{' '}
            <Link to="/settings/feature-flags" className="underline" style={{ color: 'var(--accent)' }}>
              Feature Flags
            </Link>
            .
          </span>
        </div>
      )}

      {loading ? (
        <div className="flex items-center gap-2 text-[13px] py-4" style={{ color: 'var(--text-muted)' }}>
          <Loader2 size={14} className="animate-spin" /> Loading policies…
        </div>
      ) : (
        <>
          {policies.length > 0 && (
            <div className="overflow-x-auto">
              <table className="w-full text-[13px]" style={{ color: 'var(--text-primary)' }}>
                <thead>
                  <tr
                    className="text-left text-[11px] font-medium uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    <th className="pb-2 pr-3">Product</th>
                    <th className="pb-2 pr-3">Environment</th>
                    <th className="pb-2 pr-3">Can create</th>
                    <th className="pb-2 pr-3">Must approve</th>
                    <th className="pb-2">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {policies.map((p) => (
                    <tr key={p.id} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                      <td className="py-2 pr-3">{p.product}</td>
                      <td
                        className="py-2 pr-3"
                        style={{ color: p.targetEnv ? undefined : 'var(--text-muted)' }}
                      >
                        {p.targetEnv || 'all environments'}
                      </td>
                      <td
                        className="py-2 pr-3"
                        style={{ color: p.hasCreators ? undefined : 'var(--text-muted)' }}
                      >
                        {summarizePrincipals(p.creators)}
                      </td>
                      {/* An ungated scope is the row's most important property, so it reads muted
                          rather than looking like a configured approver list. */}
                      <td
                        className="py-2 pr-3"
                        style={{ color: p.isAutoApprove ? 'var(--text-muted)' : undefined }}
                      >
                        {summarizeSteps(p.steps)}
                      </td>
                      <td className="py-2">
                        <div className="flex items-center gap-1.5">
                          <button
                            onClick={() => openEditForm(p)}
                            className="p-1 rounded-lg transition-colors hover:opacity-80"
                            style={{ color: 'var(--text-muted)' }}
                            title="Edit policy"
                          >
                            <Pencil size={14} />
                          </button>
                          <button
                            onClick={() => handleDelete(p.id)}
                            className="p-1 rounded-lg transition-colors hover:opacity-80"
                            style={{
                              color:
                                deleteConfirm === p.id ? 'var(--danger, #dc2626)' : 'var(--text-muted)',
                            }}
                            title={
                              deleteConfirm === p.id
                                ? 'Click again to confirm — this un-enrolls the scope'
                                : 'Delete policy'
                            }
                          >
                            <Trash2 size={14} />
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
            <p className="text-[13px] py-2" style={{ color: 'var(--text-muted)' }}>
              No rollback policies yet. Until one exists, only admins can create rollbacks — and each
              request has to be pushed through with an approval-gate override.
            </p>
          )}

          {/* Rows that look configured but grant nothing. Surfaced together so a half-filled policy
              is visible rather than only discovered when somebody cannot raise a rollback. */}
          {policies.some((p) => !p.hasCreators || p.isAutoApprove) && (
            <div
              className="flex items-start gap-2 px-3 py-2 rounded-lg text-[12px]"
              style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--text-secondary)' }}
            >
              <ShieldAlert size={14} className="shrink-0 mt-0.5" />
              <div className="space-y-0.5">
                {policies.some((p) => !p.hasCreators) && (
                  <p>
                    <strong>No creators set</strong> on{' '}
                    {policies
                      .filter((p) => !p.hasCreators)
                      .map((p) => `${p.product}/${p.targetEnv ?? 'all'}`)
                      .join(', ')}{' '}
                    — only admins can create rollbacks there.
                  </p>
                )}
                {policies.some((p) => p.isAutoApprove) && (
                  <p>
                    <strong>No approval required</strong> on{' '}
                    {policies
                      .filter((p) => p.isAutoApprove)
                      .map((p) => `${p.product}/${p.targetEnv ?? 'all'}`)
                      .join(', ')}{' '}
                    — rollbacks there proceed with no human decision.
                  </p>
                )}
              </div>
            </div>
          )}

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

          {showForm && (
            <div
              className="rounded-lg border p-4 space-y-3"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
            >
              <h4 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                {editingId ? 'Edit Rollback Policy' : 'New Rollback Policy'}
              </h4>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
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

                <div className="space-y-1">
                  <label className={labelClass} style={labelStyle}>
                    Target Environment
                  </label>
                  <input
                    type="text"
                    value={form.targetEnv ?? ''}
                    onChange={(e) => setField('targetEnv', e.target.value || null)}
                    placeholder="empty = all environments"
                    className={`${inputClass} w-full`}
                    style={inputStyle}
                  />
                  <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    An environment-specific policy wins over the product default, so you can require
                    two approvers for production and none for dev.
                  </p>
                </div>

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
              </div>

              {/* ── Who can create ── */}
              <div
                className="rounded-lg border p-3 space-y-3"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
              >
                <div>
                  <p
                    className="text-[11px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    Who can create rollbacks
                  </p>
                  <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
                    Anyone in these groups or listed by email. Leave both empty to allow admins only —
                    an empty list grants nobody, it never means everyone. Admins can always create.
                  </p>
                </div>

                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      AD Groups
                    </label>
                    <GroupPicker
                      values={form.creators.groups}
                      onChange={(groups) => setField('creators', { ...form.creators, groups })}
                    />
                  </div>
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      User Emails
                    </label>
                    <UserPicker
                      values={form.creators.users}
                      onChange={(users) => setField('creators', { ...form.creators, users })}
                    />
                  </div>
                </div>
              </div>

              {/* ── Who must approve ── */}
              <div
                className="rounded-lg border p-3 space-y-3"
                style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
              >
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <p
                      className="text-[11px] font-semibold uppercase tracking-wider"
                      style={{ color: 'var(--text-muted)' }}
                    >
                      Who must approve
                    </p>
                    <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
                      Every requirement across every step must be met before the rollback proceeds.
                      Steps are for grouping, not sequencing. Admins are <em>not</em> treated as
                      members of these groups — an admin clearing the gate has to use the override,
                      which is recorded with a reason.
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={addStep}
                    className="shrink-0 inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-lg transition-colors hover:opacity-80"
                    style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
                  >
                    <Plus size={13} />
                    Add Step
                  </button>
                </div>

                {form.steps.length === 0 && (
                  <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                    No steps — rollbacks in this scope need no approval and proceed immediately.
                  </p>
                )}

                {form.steps.map((step, si) => (
                  <div
                    key={si}
                    className="rounded-lg border p-3 space-y-3"
                    style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
                  >
                    <div className="flex items-center gap-2">
                      <span className="text-[11px] font-semibold" style={{ color: 'var(--text-muted)' }}>
                        Step {si + 1}
                      </span>
                      <input
                        type="text"
                        value={step.name}
                        onChange={(e) => updateStepName(si, e.target.value)}
                        placeholder="Step name (e.g. Incident commander)"
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

                    <div
                      className="space-y-2 pl-2 border-l-2"
                      style={{ borderColor: 'var(--border-color)' }}
                    >
                      {step.requirements.map((req, ri) => {
                        const err = stepErrors[`${si}:${ri}`];
                        return (
                          <div
                            key={ri}
                            className="rounded-lg border p-3 space-y-2.5"
                            style={{
                              borderColor: err ? 'var(--danger, #dc2626)' : 'var(--border-color)',
                              backgroundColor: 'var(--bg-secondary)',
                            }}
                          >
                            <div className="flex items-center gap-2">
                              <input
                                type="text"
                                value={req.name}
                                onChange={(e) => updateRequirement(si, ri, { name: e.target.value })}
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

                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                              <div className="space-y-1">
                                <label className={labelClass} style={labelStyle}>
                                  AD Groups
                                </label>
                                <GroupPicker
                                  values={req.groups}
                                  onChange={(groups) => updateRequirement(si, ri, { groups })}
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
                                  updateRequirement(si, ri, { minApprovers: Number(e.target.value) })
                                }
                                className={`${inputClass} w-full`}
                                style={inputStyle}
                              />
                            </div>

                            {err && (
                              <p className="text-[12px]" style={{ color: 'var(--danger, #dc2626)' }}>
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

              <div className="flex items-center gap-3 pt-1">
                <button
                  onClick={handleSave}
                  disabled={formSaving || !form.product.trim()}
                  className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-60"
                  style={{ backgroundColor: 'var(--accent)' }}
                >
                  {formSaving ? 'Saving…' : editingId ? 'Save Changes' : 'Create Policy'}
                </button>
                <button
                  onClick={cancelForm}
                  className="text-[13px] font-medium px-3 py-2 rounded-lg transition-colors hover:opacity-80"
                  style={{ color: 'var(--text-muted)' }}
                >
                  Cancel
                </button>
              </div>

              <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                Editing a policy affects future rollbacks only — a request already awaiting approval
                keeps the gate it was raised under.
              </p>
            </div>
          )}

          <div className="flex items-center gap-3">
            {saved && (
              <span
                className="inline-flex items-center gap-1 text-[13px]"
                style={{ color: 'var(--success)' }}
              >
                <Check size={14} /> Saved
              </span>
            )}
            {error && (
              <span className="text-[13px]" style={{ color: 'var(--danger)' }}>
                {error}
              </span>
            )}
          </div>
        </>
      )}
    </div>
  );
}
