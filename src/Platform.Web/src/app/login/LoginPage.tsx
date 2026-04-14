import { useState } from 'react';
import { Loader2, LogIn } from 'lucide-react';
import { localLogin, setStoredToken } from '@/lib/localAuth';
import { useAuthStore, createAuthUser } from '@/stores/authStore';
import { getAppName } from '@/lib/runtimeConfig';

export function LoginPage() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const setUser = useAuthStore((s) => s.setUser);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const data = await localLogin(email, password);
      setStoredToken(data.token);
      setUser(createAuthUser(data.user.id, data.user.name, data.user.email, data.user.roles));
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div
      className="flex items-center justify-center min-h-screen"
      style={{ backgroundColor: 'var(--bg-primary)' }}
    >
      <div
        className="w-full max-w-sm rounded-xl border p-8 space-y-6"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div className="text-center space-y-1">
          <h1 className="text-[18px] font-bold" style={{ color: 'var(--text-primary)' }}>
            {getAppName()}
          </h1>
          <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            Sign in to continue
          </p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          {error && (
            <div
              className="rounded-lg border p-3 text-[12px]"
              style={{
                borderColor: 'var(--danger)',
                backgroundColor: 'color-mix(in srgb, var(--danger) 8%, transparent)',
                color: 'var(--danger)',
              }}
            >
              {error}
            </div>
          )}

          <div className="space-y-1.5">
            <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Email
            </label>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="w-full rounded-lg border px-3 py-2 text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
              placeholder="admin@localhost"
              autoFocus
              required
            />
          </div>

          <div className="space-y-1.5">
            <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Password
            </label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-lg border px-3 py-2 text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-primary)',
                color: 'var(--text-primary)',
              }}
              placeholder="admin123"
              required
            />
          </div>

          <button
            type="submit"
            disabled={loading}
            className="w-full inline-flex items-center justify-center gap-2 text-[13px] font-medium px-4 py-2.5 rounded-lg text-white transition-colors hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {loading ? <Loader2 size={14} className="animate-spin" /> : <LogIn size={14} />}
            {loading ? 'Signing in...' : 'Sign in'}
          </button>
        </form>

        <div
          className="rounded-lg p-3 space-y-1.5"
          style={{ backgroundColor: 'color-mix(in srgb, var(--accent) 6%, transparent)' }}
        >
          <p className="text-[11px] font-medium" style={{ color: 'var(--text-muted)' }}>
            Dev accounts
          </p>
          <div className="space-y-0.5 text-[11px]" style={{ color: 'var(--text-muted)', fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace' }}>
            <p>admin@localhost / admin123 (Admin)</p>
            <p>user@localhost / user123 (User)</p>
            <p>viewer@localhost / viewer123 (Viewer)</p>
          </div>
        </div>
      </div>
    </div>
  );
}
