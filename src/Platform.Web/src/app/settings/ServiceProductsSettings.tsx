import { useEffect, useState } from 'react';
import { Plus, Trash2, Check, ArrowRight, History } from 'lucide-react';
import { api } from '@/lib/api';
import type { ServiceProductOverride, ServiceProductRemap } from '@/lib/api';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * Settings → Service Products: which product a service's entities belong to, when the product on the
 * payload can't be trusted.
 *
 * Product arrives as free text on every deploy event, build registration and external promotion, and
 * a pipeline being migrated keeps sending the product it was configured with long ago — so one
 * service ends up split across two products, half its history under each. Fixing the pipelines is the
 * real cure but they land one team at a time; a row here keeps the platform correct meanwhile.
 *
 * Two deliberately separate steps, which is what the page is shaped around: saving a mapping fixes
 * what arrives next and touches nothing already stored, and "Move history" is the second, reviewed
 * step that brings the existing rows across.
 */
export function ServiceProductsSettings() {
  const [rows, setRows] = useState<ServiceProductOverride[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Bumped after every write so the list has one fetch path rather than one per mutation.
  const [reloadTick, setReloadTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    api
      .listServiceProductOverrides()
      .then((r) => {
        if (!cancelled) setRows(r);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Failed to load service product overrides');
        setRows([]);
      });
    return () => {
      cancelled = true;
    };
  }, [reloadTick]);

  const reload = () => setReloadTick((t) => t + 1);

  return (
    <div className="space-y-4">
      <section
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Service product overrides
          </h2>
          <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Deploy events, builds and externally created promotions each carry the product their
            sender chose, and a pipeline mid-migration is often still sending the old one. A row here
            overrules the payload: everything new for the service is filed under the product you
            name, whatever was posted. Service and product names are matched ignoring case.
          </p>
          <p className="text-[13px] mt-2" style={{ color: 'var(--text-muted)' }}>
            Leave <strong>sent as</strong> empty to redirect the service no matter which product it
            arrives under — that is the usual case. Set it when the service name exists in more than
            one product and only one sender is wrong; a row naming a sending product wins over the
            catch-all for that sender. Saving a row changes nothing that is already stored — use{' '}
            <strong>Move history</strong> for that.
          </p>
        </div>

        <AddOverrideForm onSaved={reload} />

        {rows === null ? (
          <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            Loading…
          </p>
        ) : rows.length === 0 ? (
          <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            No overrides configured — every service is filed under the product its sender posts.
          </p>
        ) : (
          <div
            className="overflow-x-auto rounded-lg border"
            style={{ borderColor: 'var(--border-color)' }}
          >
            <table className="w-full text-[12px]">
              <thead>
                <tr
                  className="text-left text-[11px] uppercase tracking-wider"
                  style={{ color: 'var(--text-muted)' }}
                >
                  <th className="px-3 py-2 font-semibold">Service</th>
                  <th className="px-3 py-2 font-semibold">Sent as</th>
                  <th className="px-3 py-2 font-semibold">Filed under</th>
                  <th className="px-3 py-2 font-semibold">Entities</th>
                  <th className="px-3 py-2 font-semibold">Reason</th>
                  <th className="px-3 py-2 font-semibold">Updated by</th>
                  <th className="px-3 py-2 font-semibold" />
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <OverrideRow key={row.id} row={row} onChanged={reload} />
                ))}
              </tbody>
            </table>
          </div>
        )}

        {error && (
          <p className="text-[13px]" style={{ color: 'var(--danger)' }}>
            {error}
          </p>
        )}
      </section>
    </div>
  );
}

/**
 * Create-or-update form. The same POST covers both, so re-entering an existing service + sent-as
 * pair is how a mapping gets corrected — no separate edit mode to keep in sync.
 */
function AddOverrideForm({ onSaved }: { onSaved: () => void }) {
  const [service, setService] = useState('');
  const [fromProduct, setFromProduct] = useState('');
  const [product, setProduct] = useState('');
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canSave = service.trim().length > 0 && product.trim().length > 0 && !saving;

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await api.saveServiceProductOverride({
        service: service.trim(),
        product: product.trim(),
        fromProduct: fromProduct.trim() || null,
        reason: reason.trim() || null,
      });
      setService('');
      setFromProduct('');
      setProduct('');
      setReason('');
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to save the override');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div
      className="rounded-lg border p-3 space-y-3"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="grid gap-3 md:grid-cols-[1fr_1fr_1fr_1fr]">
        <label className="flex flex-col gap-1 min-w-0">
          <span className={labelClass} style={labelStyle}>
            Service
          </span>
          <input
            type="text"
            value={service}
            onChange={(e) => setService(e.target.value)}
            placeholder="swo-extension-mscsp"
            className={`${inputClass} font-mono min-w-0`}
            style={inputStyle}
          />
        </label>

        <label className="flex flex-col gap-1 min-w-0">
          <span className={labelClass} style={labelStyle}>
            Sent as (optional)
          </span>
          <input
            type="text"
            value={fromProduct}
            onChange={(e) => setFromProduct(e.target.value)}
            placeholder="any product"
            className={`${inputClass} font-mono min-w-0`}
            style={inputStyle}
          />
        </label>

        <label className="flex flex-col gap-1 min-w-0">
          <span className={labelClass} style={labelStyle}>
            Filed under
          </span>
          <input
            type="text"
            value={product}
            onChange={(e) => setProduct(e.target.value)}
            placeholder="mpt-extensions"
            className={`${inputClass} font-mono min-w-0`}
            style={inputStyle}
          />
        </label>

        <label className="flex flex-col gap-1 min-w-0">
          <span className={labelClass} style={labelStyle}>
            Reason (optional)
          </span>
          <input
            type="text"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="MPT migration wave 3"
            className={`${inputClass} min-w-0`}
            style={inputStyle}
          />
        </label>
      </div>

      <div className="flex items-center gap-3 flex-wrap">
        <button
          onClick={handleSave}
          disabled={!canSave}
          className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          <Plus size={14} />
          {saving ? 'Saving…' : 'Save override'}
        </button>
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
    </div>
  );
}

function OverrideRow({ row, onChanged }: { row: ServiceProductOverride; onChanged: () => void }) {
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [remapOpen, setRemapOpen] = useState(false);

  const handleDelete = async () => {
    setDeleting(true);
    setError(null);
    try {
      await api.deleteServiceProductOverride(row.id);
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to remove the override');
      setDeleting(false);
    }
  };

  return (
    <>
      <tr
        className="border-t"
        style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
      >
        <td className="px-3 py-2 whitespace-nowrap">
          <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
            {row.service}
          </span>
        </td>
        <td className="px-3 py-2 whitespace-nowrap font-mono">
          {row.fromProduct ?? (
            <span style={{ color: 'var(--text-muted)' }}>any</span>
          )}
        </td>
        <td className="px-3 py-2 whitespace-nowrap">
          <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
            {row.product}
          </span>
        </td>
        {/* Two numbers rather than a status word: "how much is on target" and "how much is still
            elsewhere" are the only questions this row raises, and a badge would hide both. */}
        <td className="px-3 py-2 whitespace-nowrap">
          <span style={{ color: 'var(--text-primary)' }}>{row.storedEntities}</span>
          <span style={{ color: 'var(--text-muted)' }}> on target</span>
          {row.strandedEntities > 0 && (
            <>
              <span style={{ color: 'var(--text-muted)' }}>, </span>
              <span style={{ color: 'var(--warning, #d97706)' }}>
                {row.strandedEntities} elsewhere
              </span>
            </>
          )}
        </td>
        <td className="px-3 py-2">{row.reason ?? ''}</td>
        <td className="px-3 py-2 whitespace-nowrap">{row.updatedByName}</td>
        <td className="px-3 py-2 whitespace-nowrap text-right">
          <button
            onClick={() => setRemapOpen((open) => !open)}
            className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--accent)', backgroundColor: 'var(--accent-muted)' }}
          >
            <History size={13} />
            Move history
          </button>
          <button
            onClick={handleDelete}
            disabled={deleting}
            title="Remove this override"
            className="ml-2 p-1 rounded-lg transition-colors hover:opacity-80 disabled:opacity-50"
            style={{ color: 'var(--text-muted)' }}
          >
            <Trash2 size={14} />
          </button>
        </td>
      </tr>

      {(remapOpen || error) && (
        <tr className="border-t" style={{ borderColor: 'var(--border-color)' }}>
          <td colSpan={7} className="px-3 py-3" style={{ backgroundColor: 'var(--bg-primary)' }}>
            {error && (
              <p className="text-[13px] mb-2" style={{ color: 'var(--danger)' }}>
                {error}
              </p>
            )}
            {remapOpen && <RemapPanel row={row} onApplied={onChanged} />}
          </td>
        </tr>
      )}
    </>
  );
}

/**
 * Preview-then-apply for one override's history, following the contract the Maintenance cards use: a
 * read-only count first, an explicit apply second, and the same shape reported back afterwards so
 * what happened can be compared with what was approved.
 */
function RemapPanel({ row, onApplied }: { row: ServiceProductOverride; onApplied: () => void }) {
  const [preview, setPreview] = useState<ServiceProductRemap | null>(null);
  const [result, setResult] = useState<ServiceProductRemap | null>(null);
  // Starts true and is only ever cleared: the panel mounts fresh per row, so there is no second
  // preview to reset it for.
  const [loading, setLoading] = useState(true);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Previewed on open rather than behind another button: opening the panel IS the request to see the
  // counts, and nothing is changed by looking.
  useEffect(() => {
    let cancelled = false;
    api
      .previewServiceProductRemap(row.id)
      .then((p) => {
        if (!cancelled) setPreview(p);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to preview the remap');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [row.id]);

  const handleApply = async () => {
    setApplying(true);
    setError(null);
    try {
      const applied = await api.applyServiceProductRemap(row.id);
      setResult(applied);
      setPreview(null);
      onApplied();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to move the history');
    } finally {
      setApplying(false);
    }
  };

  if (loading) {
    return (
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
        Counting…
      </p>
    );
  }

  const shown = result ?? preview;
  if (!shown) {
    return error ? (
      <p className="text-[13px]" style={{ color: 'var(--danger)' }}>
        {error}
      </p>
    ) : null;
  }

  const nothingToMove =
    shown.deployments === 0 &&
    shown.builds === 0 &&
    shown.promotions === 0 &&
    shown.retirements === 0 &&
    shown.retirementMerges === 0;

  return (
    <div className="space-y-2">
      <p className="text-[13px]" style={{ color: 'var(--text-secondary)' }}>
        {result ? 'Moved' : 'Would move'} history for{' '}
        <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
          {shown.service}
        </span>{' '}
        {shown.fromProducts.length > 0 && (
          <>
            from{' '}
            <span className="font-mono" style={{ color: 'var(--text-primary)' }}>
              {shown.fromProducts.join(', ')}
            </span>{' '}
          </>
        )}
        <ArrowRight size={12} className="inline" />{' '}
        <span className="font-mono font-medium" style={{ color: 'var(--text-primary)' }}>
          {shown.product}
        </span>
        .
      </p>

      {nothingToMove ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Nothing to move — every stored entity for this service is already filed under{' '}
          {shown.product}.
        </p>
      ) : (
        <ul className="text-[13px] space-y-0.5" style={{ color: 'var(--text-secondary)' }}>
          <li>
            <strong>{shown.deployments}</strong> deployment
            {shown.deployments === 1 ? '' : 's'}
            {shown.deployWorkItems > 0 && <> (+{shown.deployWorkItems} ticket links)</>}
          </li>
          <li>
            <strong>{shown.builds}</strong> build{shown.builds === 1 ? '' : 's'}
            {shown.buildConflicts > 0 && (
              <span style={{ color: 'var(--warning, #d97706)' }}>
                {' '}
                — {shown.buildConflicts} left in place, that version already exists under{' '}
                {shown.product}
              </span>
            )}
          </li>
          <li>
            <strong>{shown.promotions}</strong> promotion{shown.promotions === 1 ? '' : 's'}
            {shown.promotionWorkItems > 0 && <> (+{shown.promotionWorkItems} ticket links)</>}
            {shown.openPromotions > 0 && (
              <span style={{ color: 'var(--warning, #d97706)' }}>
                {' '}
                — {shown.openPromotions} still in flight
              </span>
            )}
          </li>
          {(shown.retirements > 0 || shown.retirementMerges > 0) && (
            <li>
              <strong>{shown.retirements + shown.retirementMerges}</strong> retirement record
              {shown.retirements + shown.retirementMerges === 1 ? '' : 's'}
              {shown.retirementMerges > 0 && <> ({shown.retirementMerges} merged)</>}
            </li>
          )}
        </ul>
      )}

      {shown.strandedTicketApprovals > 0 && (
        <p className="text-[13px]" style={{ color: 'var(--warning, #d97706)' }}>
          {shown.strandedTicketApprovals} recorded ticket approval
          {shown.strandedTicketApprovals === 1 ? '' : 's'} will stay under the old product. Approvals
          are keyed on ticket, product and target environment with no service, so a ticket covering
          more than one service can't be reassigned safely. A promotion still awaiting deployment may
          need approving again — prefer moving history when nothing is in flight.
        </p>
      )}

      <div className="flex items-center gap-3 flex-wrap pt-1">
        {result ? (
          <span
            className="inline-flex items-center gap-1 text-[13px]"
            style={{ color: 'var(--success)' }}
          >
            <Check size={14} /> History moved
          </span>
        ) : (
          !nothingToMove && (
            <button
              onClick={handleApply}
              disabled={applying}
              className="inline-flex items-center gap-1.5 text-[13px] font-medium px-3 py-1.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              <History size={14} />
              {applying ? 'Moving…' : 'Move history'}
            </button>
          )
        )}
        {error && (
          <span className="text-[13px]" style={{ color: 'var(--danger)' }}>
            {error}
          </span>
        )}
      </div>
    </div>
  );
}
