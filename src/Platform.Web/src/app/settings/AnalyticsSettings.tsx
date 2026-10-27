import { useEffect, useState } from 'react';
import { Check } from 'lucide-react';
import { Link } from 'react-router-dom';
import { useSettingsStore, DEFAULT_ANALYTICS } from '@/stores/settingsStore';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * How the delivery report ties an incident to the deployment that caused it when nobody said so
 * explicitly. Two knobs, both echoed in the report's definitions so a reader can see what applied.
 */
export function AnalyticsSettings() {
  const { analytics, setAnalytics } = useSettingsStore();
  const [windowDays, setWindowDays] = useState(String(analytics.attributionWindowDays));
  const [toleranceMinutes, setToleranceMinutes] = useState(String(analytics.rolloutToleranceMinutes));
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setWindowDays(String(analytics.attributionWindowDays));
    setToleranceMinutes(String(analytics.rolloutToleranceMinutes));
  }, [analytics]);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await setAnalytics({
        attributionWindowDays: Math.max(0, Math.round(Number(windowDays) || 0)),
        rolloutToleranceMinutes: Math.max(0, Math.round(Number(toleranceMinutes) || 0)),
      });
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="rounded-xl border p-5 space-y-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>Delivery metrics</h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          How the <Link to="/analytics/delivery" className="underline">Delivery metrics</Link> report ties an{' '}
          <Link to="/incidents" className="underline">incident</Link> to the deployment that caused it when the incident
          names neither a deploy nor a version: the deploy that was live when it started.
        </p>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 max-w-2xl">
        <label className="block">
          <span className={labelClass} style={labelStyle}>Attribution window (days)</span>
          <input type="number" min={0} value={windowDays} onChange={(e) => setWindowDays(e.target.value)} className={`${inputClass} w-full mt-1`} style={inputStyle} />
          <span className="block text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
            An incident this long after a deploy still counts against it. 0 means no cap: whatever version was live is to blame, however old. Default {DEFAULT_ANALYTICS.attributionWindowDays}.
          </span>
        </label>
        <label className="block">
          <span className={labelClass} style={labelStyle}>Rollout tolerance (minutes)</span>
          <input type="number" min={0} value={toleranceMinutes} onChange={(e) => setToleranceMinutes(e.target.value)} className={`${inputClass} w-full mt-1`} style={inputStyle} />
          <span className="block text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
            A deploy's time is usually when the pipeline finished; an incident that starts this many minutes earlier, mid-rollout, is still blamed on it rather than on the previous version. Default {DEFAULT_ANALYTICS.rolloutToleranceMinutes}.
          </span>
        </label>
      </div>
      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
      <div className="flex items-center gap-3">
        <button onClick={save} disabled={saving} className="px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50" style={{ backgroundColor: 'var(--accent)' }}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        {saved && <span className="inline-flex items-center gap-1 text-[12px]" style={{ color: 'var(--success)' }}><Check size={13} /> Saved</span>}
      </div>
    </section>
  );
}
