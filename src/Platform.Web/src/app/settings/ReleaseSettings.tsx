import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Check } from 'lucide-react';
import { api } from '@/lib/api';
import type { PromotionPolicyGroupRef } from '@/lib/api';
import { GroupPicker, UserPicker } from './approverPickers';
import { labelClass, labelStyle } from './formStyles';

/**
 * Who runs releases. Everyone can see the Releases page; the people and directory groups listed here
 * — and administrators — open releases, edit them and move their steps. The person named as a
 * release's manager can also run that one release. An empty list leaves releases to administrators:
 * it grants nobody, it never means everyone.
 */
export function ReleaseSettings() {
  const [groups, setGroups] = useState<PromotionPolicyGroupRef[]>([]);
  const [users, setUsers] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.getReleaseManagers()
      .then((m) => { setGroups(m.groups); setUsers(m.users); })
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load the release managers'))
      .finally(() => setLoading(false));
  }, []);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const m = await api.updateReleaseManagers({ groups, users });
      setGroups(m.groups);
      setUsers(m.users);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the release managers');
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="rounded-xl border p-5 space-y-4" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
      <div>
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>Release managers</h2>
        <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          Everyone can follow <Link to="/releases" className="underline">Releases</Link>. The people and groups here,
          and admins, open releases, edit them and move their steps. The release manager named on a release can run
          that release too. Leave both empty to keep releases to admins.
        </p>
      </div>

      {loading ? (
        <div className="skeleton h-24" />
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 max-w-3xl">
          <div className="space-y-1">
            <span className={labelClass} style={labelStyle}>AD Groups</span>
            <GroupPicker values={groups} onChange={setGroups} />
          </div>
          <div className="space-y-1">
            <span className={labelClass} style={labelStyle}>User Emails</span>
            <UserPicker values={users} onChange={setUsers} />
          </div>
        </div>
      )}

      {error && <p className="text-[12px]" style={{ color: 'var(--danger)' }}>{error}</p>}
      <div className="flex items-center gap-3">
        <button
          onClick={() => void save()}
          disabled={saving || loading}
          className="px-3 py-1.5 rounded-lg text-[13px] font-medium text-white disabled:opacity-50"
          style={{ backgroundColor: 'var(--accent)' }}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        {saved && <span className="inline-flex items-center gap-1 text-[12px]" style={{ color: 'var(--success)' }}><Check size={13} /> Saved</span>}
      </div>
    </section>
  );
}
