import { useState, useEffect, useCallback, useMemo } from 'react';
import { formatDistanceToNow } from 'date-fns';
import { Snowflake } from 'lucide-react';
import { api, type PromotionFreeze, type PromotionFreezeHistoryEntry } from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { inputClass, inputStyle } from './formStyles';

const errorStyle = {
  color: 'var(--danger, #dc2626)',
  backgroundColor: 'var(--danger-muted, #fee2e2)',
};

const JOURNAL_LIMIT = 10;

interface Pair {
  product: string;
  targetEnv: string;
}

function ActiveFreezeCard({
  freeze,
  envLabel,
  onLift,
}: {
  freeze: PromotionFreeze;
  envLabel: string;
  onLift: () => Promise<void>;
}) {
  const [confirming, setConfirming] = useState(false);
  const [lifting, setLifting] = useState(false);

  // In flight from the confirming click until the refresh completes, so a further click cannot
  // send a second DELETE. On failure the parent shows the error and the button is re-enabled.
  const handleClick = async () => {
    if (!confirming) {
      setConfirming(true);
      return;
    }
    setConfirming(false);
    setLifting(true);
    try {
      await onLift();
    } finally {
      setLifting(false);
    }
  };

  return (
    <div
      className="rounded-lg border p-3 flex items-start gap-3"
      style={{
        borderColor: 'var(--border-color)',
        borderLeft: '3px solid var(--info)',
        backgroundColor: 'var(--bg-primary)',
      }}
    >
      <Snowflake size={14} className="mt-0.5 shrink-0" style={{ color: 'var(--info)' }} />
      <div className="flex-1 min-w-0 space-y-1">
        <div className="flex items-center gap-2">
          <span className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            {freeze.product} → {envLabel}
          </span>
          <span
            className="text-[11px] px-1.5 py-0.5 rounded-md"
            style={{ backgroundColor: 'var(--info-bg)', color: 'var(--info)' }}
          >
            {freeze.heldCandidates} held
          </span>
        </div>
        <p className="text-[13px] break-words" style={{ color: 'var(--text-primary)' }}>
          {freeze.reason}
        </p>
        <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          frozen by {freeze.frozenBy} · since{' '}
          {formatDistanceToNow(new Date(freeze.frozenAt), { addSuffix: true })}
        </p>
      </div>
      <button
        type="button"
        onClick={handleClick}
        onBlur={() => setConfirming(false)}
        disabled={lifting}
        className="px-3 py-1.5 rounded-lg text-[13px] font-medium border shrink-0 transition-opacity hover:opacity-80 disabled:opacity-50 disabled:cursor-not-allowed"
        style={{
          borderColor: confirming ? 'var(--border-strong)' : 'var(--border-color)',
          backgroundColor: 'transparent',
          color: 'var(--text-primary)',
        }}
      >
        {lifting ? 'Lifting…' : confirming ? 'Click again to confirm' : 'Lift freeze'}
      </button>
    </div>
  );
}

function FreezeJournal({
  entries,
  envLabel,
}: {
  entries: PromotionFreezeHistoryEntry[];
  envLabel: (env: string) => string;
}) {
  if (entries.length === 0) {
    return (
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
        No freeze activity yet.
      </p>
    );
  }

  return (
    <ol>
      {entries.map((entry, idx) => {
        const frozen = entry.action === 'frozen';
        const last = idx === entries.length - 1;
        return (
          <li key={`${entry.at}:${entry.action}:${entry.product}:${entry.targetEnv}:${idx}`} className="flex gap-3">
            <div className="flex flex-col items-center w-3 shrink-0">
              <span
                className="w-2 h-2 rounded-full mt-1.5 shrink-0"
                style={{ backgroundColor: frozen ? 'var(--warning)' : 'var(--success)' }}
              />
              {!last && (
                <span className="flex-1 w-px" style={{ backgroundColor: 'var(--border-color)' }} />
              )}
            </div>
            <div className={`flex-1 flex items-start justify-between gap-3 ${last ? '' : 'pb-3'}`}>
              <p className="text-[13px] break-words min-w-0" style={{ color: 'var(--text-primary)' }}>
                <span className="font-medium">{entry.actor}</span> {frozen ? 'froze' : 'lifted'}{' '}
                {entry.product} → {envLabel(entry.targetEnv)}
                {entry.reason && (
                  <span style={{ color: 'var(--text-secondary)' }}> — “{entry.reason}”</span>
                )}
              </p>
              <span
                className="text-[11px] whitespace-nowrap mt-0.5"
                style={{ color: 'var(--text-muted)' }}
              >
                {formatDistanceToNow(new Date(entry.at), { addSuffix: true })}
              </span>
            </div>
          </li>
        );
      })}
    </ol>
  );
}

export function FreezeSettings() {
  const { getDisplayName, getOrderedEnvironments } = useSettingsStore();

  const [freezes, setFreezes] = useState<PromotionFreeze[]>([]);
  const [history, setHistory] = useState<PromotionFreezeHistoryEntry[]>([]);
  const [historyError, setHistoryError] = useState(false);
  const [pairs, setPairs] = useState<Pair[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // The initial load failed: the list and the form would be built from missing data.
  const [loadFailed, setLoadFailed] = useState(false);

  const [product, setProduct] = useState('');
  const [env, setEnv] = useState('');
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);

  const loadFreezes = useCallback(async () => {
    const res = await api.listPromotionFreezes();
    setFreezes(res.freezes);
  }, []);

  // Ask for one more than we show: getting it back is how we know the journal is truncated.
  // Never rejects: a journal problem must not break the active list, the form or a mutation.
  const loadHistory = useCallback(async () => {
    try {
      const res = await api.listPromotionFreezeHistory(JOURNAL_LIMIT + 1);
      setHistory(res.entries);
      setHistoryError(false);
    } catch {
      setHistoryError(true);
    }
  }, []);

  useEffect(() => {
    // The loaders only set state after their request resolves; the rule cannot see past the call.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    Promise.all([loadFreezes(), loadHistory(), api.listPromotionPolicies()])
      .then(([, , res]) => {
        const seen = new Set<string>();
        const distinct: Pair[] = [];
        for (const p of res.policies) {
          const key = `${p.product}\u0000${p.targetEnv}`;
          if (seen.has(key)) continue;
          seen.add(key);
          distinct.push({ product: p.product, targetEnv: p.targetEnv });
        }
        setPairs(distinct);
      })
      .catch((e) => {
        setLoadFailed(true);
        setError(e instanceof Error ? e.message : 'Failed to load freezes');
      })
      .finally(() => setLoading(false));
  }, [loadFreezes, loadHistory]);

  const products = useMemo(
    () => Array.from(new Set(pairs.map((p) => p.product))).sort(),
    [pairs],
  );

  const envOptions = useMemo(() => {
    const frozen = new Set(freezes.filter((f) => f.product === product).map((f) => f.targetEnv));
    const keys = pairs
      .filter((p) => p.product === product && !frozen.has(p.targetEnv))
      .map((p) => p.targetEnv);
    return getOrderedEnvironments(keys);
  }, [pairs, freezes, product, getOrderedEnvironments]);

  const canFreeze = product !== '' && env !== '' && reason.trim() !== '' && !saving;

  const handleFreeze = async () => {
    if (!canFreeze) return;
    setSaving(true);
    setError(null);
    try {
      await api.createPromotionFreeze({ product, targetEnv: env, reason: reason.trim() });
      await loadFreezes();
      setProduct('');
      setEnv('');
      setReason('');
      await loadHistory();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to freeze');
    } finally {
      setSaving(false);
    }
  };

  const handleLift = async (f: PromotionFreeze) => {
    setError(null);
    try {
      await api.liftPromotionFreeze(f.product, f.targetEnv);
      await loadFreezes();
      await loadHistory();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to lift freeze');
    }
  };

  return (
    <div
      className="rounded-xl border p-5 space-y-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Freeze
        </h2>
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Hold promotions for a product into an environment. Approvals keep being recorded; nothing
          becomes Approved until the freeze is lifted.
        </p>
      </div>

      {error && (
        <div className="text-[13px] rounded-lg px-3 py-2" style={errorStyle}>
          {error}
        </div>
      )}

      {loading ? (
        <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Loading…
        </p>
      ) : (
        <>
          <section className="space-y-2">
            <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
              Active freezes
              {freezes.length > 0 && (
                <span style={{ color: 'var(--text-muted)' }}> · {freezes.length}</span>
              )}
            </h3>
            {freezes.length === 0 ? (
              !loadFailed && (
                <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                  No active freezes. Promotions flow normally.
                </p>
              )
            ) : (
              freezes.map((f) => (
                <ActiveFreezeCard
                  key={`${f.product}:${f.targetEnv}`}
                  freeze={f}
                  envLabel={getDisplayName(f.targetEnv)}
                  onLift={() => handleLift(f)}
                />
              ))
            )}
          </section>

          {!loadFailed && (
            <section className="space-y-2">
              <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
                Freeze a product
              </h3>
              <div className="flex flex-wrap items-center gap-2">
                <select
                  value={product}
                  onChange={(e) => {
                    setProduct(e.target.value);
                    setEnv('');
                  }}
                  className={inputClass}
                  style={inputStyle}
                  aria-label="Product"
                >
                  <option value="">Product…</option>
                  {products.map((p) => (
                    <option key={p} value={p}>
                      {p}
                    </option>
                  ))}
                </select>
                <select
                  value={env}
                  onChange={(e) => setEnv(e.target.value)}
                  disabled={product === ''}
                  className={`${inputClass} disabled:opacity-50`}
                  style={inputStyle}
                  aria-label="Environment"
                >
                  <option value="">
                    {product !== '' && envOptions.length === 0 ? 'All frozen' : 'Environment…'}
                  </option>
                  {envOptions.map((k) => (
                    <option key={k} value={k}>
                      {getDisplayName(k)}
                    </option>
                  ))}
                </select>
                <input
                  type="text"
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') handleFreeze();
                  }}
                  maxLength={1000}
                  placeholder="Reason (required)"
                  aria-label="Reason"
                  className={`${inputClass} flex-1 min-w-[200px]`}
                  style={inputStyle}
                />
                <button
                  type="button"
                  onClick={handleFreeze}
                  disabled={!canFreeze}
                  className="px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50 disabled:cursor-not-allowed transition-opacity"
                  style={{ backgroundColor: 'var(--accent)' }}
                >
                  {saving ? 'Freezing…' : 'Freeze'}
                </button>
              </div>
              <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                Approvals keep being recorded while frozen; nothing is promoted until you lift it.
              </p>
            </section>
          )}

          <section className="space-y-2">
            <h3 className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
              Journal
            </h3>
            {historyError && (
              <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
                Journal unavailable.
              </p>
            )}
            {!historyError && (
              <FreezeJournal entries={history.slice(0, JOURNAL_LIMIT)} envLabel={getDisplayName} />
            )}
            {!historyError && history.length > JOURNAL_LIMIT && (
              <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                Showing latest {JOURNAL_LIMIT}
              </p>
            )}
          </section>
        </>
      )}
    </div>
  );
}
