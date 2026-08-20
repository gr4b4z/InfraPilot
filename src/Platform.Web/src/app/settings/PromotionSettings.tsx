import { useState, useEffect, useMemo } from 'react';
import { useAuthStore } from '@/stores/authStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { roleDisplay, useConfiguredRoles } from '@/lib/roleLabel';
import { canonicaliseRoleKey } from '@/lib/roleKey';
import {
  api,
  type PromotionPolicy,
  type PromotionPolicyStep,
  type PromotionPolicyRequirement,
  type UpsertPromotionPolicyPayload,
} from '@/lib/api';
import { AlertTriangle, Plus, Trash2, Check, Pencil, X } from 'lucide-react';
// Directory pickers and form styling are shared with the rollback policy editor — both configure
// approvers from the same group/user vocabulary under the same server-side matching rules.
import { UserPicker, GroupPicker } from './approverPickers';
import { ComboBox, type ComboOption } from '@/components/ui/ComboBox';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/** The synthetic source env for promotions straight from the build registry. */
const BUILD_SOURCE_ENV = 'build';

/** The branch patterns an admin is most likely to want on a build → dev edge. */
const SUGGESTED_BRANCH_PATTERNS = ['refs/heads/main', 'refs/heads/master', 'refs/heads/release/*'];

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
  sourceEnv: '',
  targetEnv: '',
  steps: [],
  tracksWorkItems: true,
  requiredWorkItemRoles: [],
  escalationGroup: null,
  requireAllWorkItemsApproved: false,
  autoApproveOnAllWorkItemsApproved: false,
  autoApproveWhenNoWorkItems: false,
  sourceRequiresDeploy: true,
  autoCreateFromBranches: [],
  approvedWebhookDelaySeconds: null,
};

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

/**
 * Which participant roles every work item on this edge must have somebody in. Toggle buttons over the
 * configured role vocabulary (Settings → Participant Roles) rather than a typeahead: the vocabulary is
 * short, curated, and already on screen elsewhere on this page, so free text would only ever produce a
 * requirement no work item can satisfy.
 *
 * A role that was picked before an admin removed it from the vocabulary is still rendered — as its raw
 * canonical key — so an existing requirement can be seen and cleared instead of silently persisting
 * through every save.
 */
function RequiredRolesPicker({
  values,
  onChange,
  disabled = false,
}: {
  values: string[];
  onChange: (next: string[]) => void;
  /** Set while the edge creates no work items — the picks are kept, just not editable. */
  disabled?: boolean;
}) {
  const configured = useConfiguredRoles();
  const options = useMemo(() => {
    const out = configured.map((r) => ({ key: r.key, label: r.displayName, known: true }));
    for (const v of values) {
      const key = canonicaliseRoleKey(v);
      if (!key || out.some((o) => o.key === key)) continue;
      out.push({ key, label: key, known: false });
    }
    return out;
  }, [configured, values]);

  const toggle = (key: string) => {
    onChange(values.includes(key) ? values.filter((v) => v !== key) : [...values, key]);
  };

  if (options.length === 0) {
    return (
      <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
        No participant roles are configured yet — add some under Settings → Participant Roles first.
      </p>
    );
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {options.map((o) => {
        const selected = values.includes(o.key);
        return (
          <button
            key={o.key}
            type="button"
            onClick={() => toggle(o.key)}
            aria-pressed={selected}
            disabled={disabled}
            className="inline-flex items-center gap-1 text-[12px] font-medium px-2.5 py-1 rounded-full border transition-colors disabled:cursor-not-allowed"
            style={{
              borderColor: selected ? 'var(--accent)' : 'var(--border-color)',
              backgroundColor: selected ? 'var(--accent-bg)' : 'var(--bg-primary)',
              color: selected ? 'var(--accent)' : 'var(--text-secondary)',
            }}
            title={o.known ? o.key : `${o.key} — not a configured participant role`}
          >
            {selected && <Check size={11} />}
            {o.label}
            {!o.known && <AlertTriangle size={11} />}
          </button>
        );
      })}
    </div>
  );
}

/**
 * Chip editor for auto-create branch patterns. The three suggestions cover what admins actually
 * configure (trunk + release lines); anything else is typed and added with Enter. Free text stays
 * possible because branch layouts vary per repo — the server only normalises, never restricts.
 */
function BranchPatternsEditor({
  values,
  onChange,
}: {
  values: string[];
  onChange: (next: string[]) => void;
}) {
  const [draft, setDraft] = useState('');

  const add = (pattern: string) => {
    const p = pattern.trim();
    if (!p || values.includes(p)) return;
    onChange([...values, p]);
    setDraft('');
  };

  return (
    <div className="space-y-1.5">
      {values.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {values.map((v) => (
            <span
              key={v}
              className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full border text-[12px] font-mono"
              style={{
                borderColor: 'var(--accent)',
                backgroundColor: 'var(--accent-bg)',
                color: 'var(--accent)',
              }}
            >
              {v}
              <button
                type="button"
                onClick={() => onChange(values.filter((x) => x !== v))}
                aria-label={`Remove ${v}`}
                className="transition-opacity hover:opacity-70"
              >
                <X size={11} />
              </button>
            </span>
          ))}
        </div>
      )}
      <div className="flex items-center gap-1.5">
        <input
          type="text"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              add(draft);
            }
          }}
          placeholder="refs/heads/… (Enter to add)"
          aria-label="Add branch pattern"
          className={`${inputClass} w-64`}
          style={inputStyle}
        />
      </div>
      <div className="flex flex-wrap items-center gap-1.5">
        {SUGGESTED_BRANCH_PATTERNS.filter((s) => !values.includes(s)).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => add(s)}
            className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full border text-[11px] font-mono transition-colors hover:opacity-80"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}
          >
            <Plus size={10} />
            {s}
          </button>
        ))}
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
  // How many pending promotions the last save re-gated (null until a save reports it).
  const [reapplied, setReapplied] = useState<number | null>(null);

  // ── Form state (inline add/edit) ──
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<UpsertPromotionPolicyPayload>(emptyForm);
  const [formSaving, setFormSaving] = useState(false);
  const [stepErrors, setStepErrors] = useState<Record<string, string>>({});

  // ── Delete confirm ──
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  // ── Scope-field suggestions ──
  // Values the scope combo boxes offer. Products come from the deployment overview (anything that
  // has ever reported a deploy); services from the selected product's state matrix. Both stay free
  // text — a policy is often created before its product's first deploy or build.
  const [knownProducts, setKnownProducts] = useState<string[]>([]);
  // Keyed by the product they were fetched for, so a mid-edit product change renders no stale
  // suggestions while the refetch is in flight (and an empty product renders none at all).
  const [knownServices, setKnownServices] = useState<{ product: string; services: string[] }>({
    product: '',
    services: [],
  });
  const environments = useSettingsStore((s) => s.environments);

  // ── Load data ──
  useEffect(() => {
    if (!isAdmin) return;
    api
      .listPromotionPolicies()
      .then((d) => setPolicies(d.policies))
      .catch(() => setPolError('Failed to load policies'))
      .finally(() => setPolLoading(false));
  }, [isAdmin]);

  useEffect(() => {
    if (!isAdmin || !showForm) return;
    let cancelled = false;
    api
      .getDeploymentProducts()
      .then((products) => {
        if (!cancelled) setKnownProducts(products.map((p) => p.product));
      })
      .catch(() => {
        // Suggestions only — the field still takes free text.
        if (!cancelled) setKnownProducts([]);
      });
    return () => {
      cancelled = true;
    };
  }, [isAdmin, showForm]);

  const formProduct = form.product.trim();
  useEffect(() => {
    if (!isAdmin || !showForm || !formProduct) return;
    let cancelled = false;
    // Debounced: the product field is typeable, and each keystroke would otherwise hit the state
    // endpoint for a product that doesn't exist yet.
    const timer = setTimeout(() => {
      api
        .getDeploymentState({ product: formProduct })
        .then((rows) => {
          if (cancelled) return;
          setKnownServices({
            product: formProduct,
            services: Array.from(new Set(rows.map((r) => r.service))).sort(),
          });
        })
        .catch(() => {
          if (!cancelled) setKnownServices({ product: formProduct, services: [] });
        });
    }, 300);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [isAdmin, showForm, formProduct]);

  if (!isAdmin) return null;

  // ── Combo-box option lists ──
  // Union with values already used by saved policies, so anything configured before (or outside)
  // the deploy history still shows up as a pick.
  const productOptions: ComboOption[] = Array.from(
    new Set([...knownProducts, ...policies.map((p) => p.product)]),
  )
    .sort()
    .map((p) => ({ value: p, hint: knownProducts.includes(p) ? 'reports deployments' : 'used by an existing policy' }));

  const serviceOptions: ComboOption[] =
    knownServices.product === formProduct
      ? knownServices.services.map((s) => ({ value: s }))
      : [];

  const envOptions: ComboOption[] = environments.map((e) => ({
    value: e.key,
    hint: e.displayName !== e.key ? e.displayName : undefined,
  }));
  // "build" first on the source side: it's the option people won't guess — the synthetic source
  // for deploying registered builds (main auto-deploys, feature-branch deploys on demand).
  const sourceEnvOptions: ComboOption[] = [
    {
      value: BUILD_SOURCE_ENV,
      hint: 'the build registry — promote registered builds (no source deploys needed)',
    },
    ...envOptions.filter((o) => o.value !== BUILD_SOURCE_ENV),
  ];

  const isBuildEdge = form.sourceEnv.trim().toLowerCase() === BUILD_SOURCE_ENV;

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
      sourceEnv: p.sourceEnv,
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
      tracksWorkItems: p.tracksWorkItems ?? true,
      requiredWorkItemRoles: [...(p.requiredWorkItemRoles ?? [])],
      escalationGroup: p.escalationGroup,
      requireAllWorkItemsApproved: p.requireAllWorkItemsApproved ?? false,
      autoApproveOnAllWorkItemsApproved: p.autoApproveOnAllWorkItemsApproved ?? false,
      autoApproveWhenNoWorkItems: p.autoApproveWhenNoWorkItems ?? false,
      sourceRequiresDeploy: p.sourceRequiresDeploy ?? true,
      autoCreateFromBranches: [...(p.autoCreateFromBranches ?? [])],
      approvedWebhookDelaySeconds: p.approvedWebhookDelaySeconds ?? null,
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
      // The save re-gated any pending promotions on this edge; say how many so the operator knows
      // the change reached in-flight work and isn't only forward-looking.
      setReapplied(result.reappliedCandidates ?? 0);
      setTimeout(() => setPolSaved(false), 4000);
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

  // Picking the synthetic "build" source also unchecks "require a source deploy": nothing is ever
  // deployed *to* the build registry, so leaving it on would make every promotion on the edge fail.
  // One-shot on the transition, not enforced — an admin who re-checks it keeps their choice.
  const setSourceEnv = (value: string) => {
    setForm((prev) => {
      const becameBuild =
        value.trim().toLowerCase() === BUILD_SOURCE_ENV &&
        prev.sourceEnv.trim().toLowerCase() !== BUILD_SOURCE_ENV;
      return {
        ...prev,
        sourceEnv: value,
        sourceRequiresDeploy: becameBuild ? false : prev.sourceRequiresDeploy,
      };
    });
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
                      <th className="pb-2 pr-3">Edge</th>
                      <th className="pb-2 pr-3">Approval Steps</th>
                      <th className="pb-2 pr-3">Required Roles</th>
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
                        <td className="py-2 pr-3">{p.sourceEnv} → {p.targetEnv}</td>
                        <td
                          className="py-2 pr-3"
                          style={{
                            color: p.steps?.length ? undefined : 'var(--text-muted)',
                          }}
                        >
                          {summarizeSteps(p.steps)}
                        </td>
                        {/* "No work items" wins over the role list: it's the reason the roles don't
                           apply, and it's the row's most important property when set. */}
                        <td
                          className="py-2 pr-3"
                          style={{
                            color:
                              p.tracksWorkItems === false || !p.requiredWorkItemRoles?.length
                                ? 'var(--text-muted)'
                                : undefined,
                          }}
                        >
                          {p.tracksWorkItems === false
                            ? 'no work items'
                            : p.requiredWorkItemRoles?.length
                              ? p.requiredWorkItemRoles.map((r) => roleDisplay({ role: r })).join(', ')
                              : '—'}
                        </td>
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

                <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
                  A policy gates one promotion edge: who may move <em>this product</em> (optionally
                  one service) from <em>source</em> to <em>target</em>. Names must match what
                  pipelines report — every field suggests known values, but new ones can be typed
                  before their first deploy or build arrives.
                </p>

                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  {/* Product */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Product *
                    </label>
                    <ComboBox
                      value={form.product}
                      onChange={(v) => setField('product', v)}
                      options={productOptions}
                      placeholder="e.g. my-product"
                      ariaLabel="Product"
                    />
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      Exactly as pipelines report it in deploy events and build registrations —
                      the suggestions are products the platform has already seen.
                    </p>
                  </div>

                  {/* Service */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Service
                    </label>
                    <ComboBox
                      value={form.service ?? ''}
                      onChange={(v) => setField('service', v || null)}
                      options={serviceOptions}
                      placeholder="empty = product-default"
                      ariaLabel="Service"
                    />
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      Leave empty to make this the product default, covering every service that has
                      no policy of its own. A service-specific policy always wins over the default.
                    </p>
                  </div>

                  {/* Source Env */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Source Env *
                    </label>
                    <ComboBox
                      value={form.sourceEnv}
                      onChange={setSourceEnv}
                      options={sourceEnvOptions}
                      placeholder="e.g. staging — or build"
                      ariaLabel="Source environment"
                    />
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      Where versions are promoted <em>from</em>. Pick{' '}
                      <code>build</code> to promote registered builds directly — that enables
                      &ldquo;Deploy a build&rdquo; on the service page and the branch-based
                      auto-create below.
                    </p>
                  </div>

                  {/* Target Env */}
                  <div className="space-y-1">
                    <label className={labelClass} style={labelStyle}>
                      Target Env *
                    </label>
                    <ComboBox
                      value={form.targetEnv}
                      onChange={(v) => setField('targetEnv', v)}
                      options={envOptions}
                      placeholder="e.g. production"
                      ariaLabel="Target environment"
                    />
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      The environment this policy guards — promotions landing here wait for the
                      approvals configured below. Suggestions come from Settings → Environments.
                    </p>
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
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      Optional group to notify when a pending promotion sits unapproved too long.
                    </p>
                  </div>
                </div>

                {/* ── Build promotions ── only meaningful on a build → * edge, so only shown there.
                    (Hidden rather than dimmed, unlike the work-item blocks: on a non-build edge these
                    settings aren't dormant, they're meaningless — the hook never reads them.) */}
                {isBuildEdge && (
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
                      Build promotions
                    </p>
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      This edge promotes straight from the build registry. Builds from the branch
                      patterns below open a promotion here <em>automatically</em> the moment they
                      register (with an empty approval list that means auto-deploy — the main → dev
                      pattern). Builds from any other branch wait until somebody picks them via
                      &ldquo;Deploy a build&rdquo;.
                    </p>

                    <div className="space-y-1">
                      <label className={labelClass} style={labelStyle}>
                        Auto-create from branches
                      </label>
                      <BranchPatternsEditor
                        values={form.autoCreateFromBranches}
                        onChange={(next) => setField('autoCreateFromBranches', next)}
                      />
                      <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                        Full git refs; <code>*</code> matches anything (e.g.{' '}
                        <code>refs/heads/release/*</code>). Leave empty while rolling out — builds
                        then only deploy when explicitly picked.
                      </p>
                    </div>

                    <div className="space-y-1">
                      <label className={labelClass} style={labelStyle}>
                        Approved-webhook delay (seconds)
                      </label>
                      <input
                        type="number"
                        min={0}
                        max={3600}
                        value={form.approvedWebhookDelaySeconds ?? ''}
                        onChange={(e) =>
                          setField(
                            'approvedWebhookDelaySeconds',
                            e.target.value === '' ? null : Math.max(0, Number(e.target.value)),
                          )
                        }
                        placeholder="default (10)"
                        className={`${inputClass} w-32`}
                        style={inputStyle}
                      />
                      <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                        How long an approval can be undone before the deploy webhook fires. Set{' '}
                        <code>0</code> on auto-approved edges — an undo window on an automatic
                        deploy is pure latency.
                      </p>
                    </div>
                  </div>
                )}

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

                              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
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

                {/* ── Work-item tracking ──
                    The switch everything else in this section hangs off: an edge that creates no work
                    items has nothing to require people on and nothing to gate. Its own block, first,
                    so the two blocks below read as consequences of it. */}
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
                    Work items
                  </p>

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.tracksWorkItems}
                      onChange={(e) => setField('tracksWorkItems', e.target.checked)}
                      className="mt-0.5 rounded"
                    />
                    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
                      Create work items for promotions on this edge
                      <span
                        className="block text-[11px] mt-0.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        Uncheck for edges whose target isn&rsquo;t ready for QA — a developer
                        integration environment, a CI test ring (e.g. dev &rarr; test). No work items
                        are created, so nothing reaches the work-items queue and nothing needs a
                        sign-off. The promotion still records which work items it carries.
                      </span>
                    </span>
                  </label>
                </div>

                {/* ── Required work-item roles ──
                    Who has to be named on each work item. Its own block, above the gate options,
                    because it isn't a gate: nothing here blocks an approval — it marks work items as
                    incomplete and asks somebody to fill the role. */}
                <div
                  className="rounded-lg border p-3 space-y-2"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-secondary)',
                    // Dimmed rather than hidden when work items are off: the settings are still stored
                    // and come back the moment tracking is re-enabled, so hiding them would make a
                    // saved configuration look lost.
                    opacity: form.tracksWorkItems ? 1 : 0.55,
                  }}
                >
                  <p
                    className="text-[11px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    Required work-item roles
                  </p>
                  <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                    {form.tracksWorkItems ? (
                      <>
                        Every work item promoted over this edge must have somebody in each role picked
                        here. Items missing one are flagged as needing attention across the promotions
                        list, the promotion page and the work-items queue, and show up under its
                        &ldquo;Not assigned&rdquo; tab. This does not block approval.
                      </>
                    ) : (
                      <>Not applicable — this edge creates no work items.</>
                    )}
                  </p>
                  <RequiredRolesPicker
                    values={form.requiredWorkItemRoles}
                    onChange={(next) => setField('requiredWorkItemRoles', next)}
                    disabled={!form.tracksWorkItems}
                  />
                </div>

                {/* ── Work-item-gate options ── */}
                <div
                  className="rounded-lg border p-3 space-y-2"
                  style={{
                    borderColor: 'var(--border-color)',
                    backgroundColor: 'var(--bg-secondary)',
                    opacity: form.tracksWorkItems ? 1 : 0.55,
                  }}
                >
                  <p
                    className="text-[11px] font-semibold uppercase tracking-wider"
                    style={{ color: 'var(--text-muted)' }}
                  >
                    Work-item-gate options
                  </p>
                  {!form.tracksWorkItems && (
                    <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                      The first three don&rsquo;t apply — this edge creates no work items.
                    </p>
                  )}

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.requireAllWorkItemsApproved}
                      onChange={(e) => setField('requireAllWorkItemsApproved', e.target.checked)}
                      disabled={!form.tracksWorkItems}
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
                      disabled={!form.tracksWorkItems}
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
                      disabled={!form.tracksWorkItems}
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

                  <label className="flex items-start gap-2.5 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={form.sourceRequiresDeploy}
                      onChange={(e) => setField('sourceRequiresDeploy', e.target.checked)}
                      className="mt-0.5 rounded"
                    />
                    <span className="text-[13px]" style={{ color: 'var(--text-primary)' }}>
                      Require a succeeded deploy in the source environment
                      <span
                        className="block text-[11px] mt-0.5"
                        style={{ color: 'var(--text-muted)' }}
                      >
                        Uncheck for edges whose source never receives deployments — the{' '}
                        <code>build</code> registry (unchecked automatically when you pick it) or a
                        CI landing zone / release track. Also disables the source-drift check for
                        this edge.
                      </span>
                    </span>
                  </label>
                </div>

                {/* Form actions */}
                <div className="flex items-center gap-2 pt-1">
                  <button
                    onClick={handleSavePolicy}
                    disabled={formSaving || !form.product.trim() || !form.sourceEnv.trim() || !form.targetEnv.trim()}
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

                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  Saving re-applies these settings to promotions that are still pending on this edge,
                  not just future ones. Promotions that are already approved or deploying keep the
                  rules they were approved under.
                </p>
              </div>
            )}

            {polSaved && (
              <span
                className="inline-flex items-center gap-1 text-[13px]"
                style={{ color: 'var(--success)' }}
              >
                <Check size={14} /> Saved
                {reapplied ? (
                  <>
                    {' — re-applied to '}
                    {reapplied} pending promotion{reapplied === 1 ? '' : 's'}
                  </>
                ) : null}
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
