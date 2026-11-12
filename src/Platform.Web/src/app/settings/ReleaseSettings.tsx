import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check } from 'lucide-react';
import { api } from '@/lib/api';
import type { ReleaseManagers } from '@/lib/api';
import { EnvLabel } from '@/components/environments/EnvBadge';
import { inputClass, inputStyle, labelClass, labelStyle } from './formStyles';

/**
 * Who runs releases. Everyone can see the Releases page; admins and the release managers open
 * releases, edit them and move their steps. Release managers are not a list of their own: they are the
 * approvers of one requirement — "Release Manager" unless changed here — on each product's promotion
 * gate into production, so the people who give the release approval are the people who run the
 * release, and both are kept in one place, Settings → Promotions.
 */
export function ReleaseSettings() {
  const [data, setData] = useState<ReleaseManagers | null>(null);
  const [requirement, setRequirement] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getReleaseManagers()
      .then((m) => { setData(m); setRequirement(m.requirement); })
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load the release managers'))
      .finally(() => setLoading(false));
  }, []);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const m = await api.updateReleaseManagers({ requirement: requirement.trim() });
      setData(m);
      setRequirement(m.requirement);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save');
    } finally {
      setSaving(false);
    }
  };

  const th = 'text-left font-medium px-2 py-1.5 whitespace-nowrap';
  const td = 'px-2 py-2 align-top';
  const unchanged = data !== null && requirement.trim() === data.requirement;

  return (
    <section className="rounded-xl border p-5 space-y-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>Release managers</h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Everyone can follow <Link to="/releases" className="underline">Releases</Link>. Admins and the release managers open
          releases, edit them and move their steps. The release managers of a product are the approvers of this requirement
          on its promotion gate into production; change who they are in{' '}
          <Link to="/settings/promotions" className="underline">Settings → Promotions</Link>. The release manager named on a
          release can run that release too.
        </p>
      </div>

      {loading ? (
        <div className="skeleton h-24" />
      ) : (
        <>
          <label className="block max-w-md">
            <span className={labelClass} style={labelStyle}>Gate requirement</span>
            <input
              value={requirement}
              onChange={(e) => setRequirement(e.target.value)}
              list="release-manager-requirements"
              placeholder="Release Manager"
              className={`${inputClass} w-full mt-1`}
              style={inputStyle}
            />
            <datalist id="release-manager-requirements">
              {(data?.knownRequirements ?? []).map((r) => <option key={r} value={r} />)}
            </datalist>
            <span className="block text-[12px] mt-1" style={{ color: 'var(--text-muted)' }}>
              The name of the approval requirement, as the promotion gates call it.
            </span>
          </label>

          <div className="flex items-center gap-3">
            <button
              onClick={() => void save()}
              disabled={saving || !requirement.trim() || unchanged}
              className="px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)' }}
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
            {saved && <span className="inline-flex items-center gap-1 text-[12px]" style={{ color: 'var(--success)' }}><Check size={13} /> Saved</span>}
          </div>

          <div>
            <div className={labelClass} style={labelStyle}>Who that makes release managers</div>
            {data && data.gates.length === 0 ? (
              <p className="text-[13px] mt-1" style={{ color: 'var(--text-secondary)' }}>
                No promotion gate into a production environment has a “{data.requirement}” requirement, so only admins run
                releases.
              </p>
            ) : (
              <div className="overflow-x-auto mt-1">
                <table className="w-full text-[12px]">
                  <thead style={{ color: 'var(--text-muted)' }}>
                    <tr>
                      <th className={th}>Product</th>
                      <th className={th}>Gate</th>
                      <th className={th}>Step</th>
                      <th className={th}>Approvers</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data?.gates.map((g) => (
                      <tr key={`${g.policyId}/${g.step}/${g.requirement}`} className="border-t" style={{ borderColor: 'var(--border-color)' }}>
                        <td className={td}>{g.product}{g.service && <span style={{ color: 'var(--text-muted)' }}> · {g.service}</span>}</td>
                        <td className={`${td} whitespace-nowrap`}><EnvLabel env={g.sourceEnv} /> → <EnvLabel env={g.targetEnv} /></td>
                        <td className={td} style={{ color: 'var(--text-secondary)' }}>{g.step} › {g.requirement}</td>
                        <td className={td} style={{ color: 'var(--text-secondary)' }}>
                          {[...g.users, ...g.groups.map((x) => `${x.name || x.id} (group)`)].join(', ') || 'nobody'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}

      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
    </section>
  );
}
