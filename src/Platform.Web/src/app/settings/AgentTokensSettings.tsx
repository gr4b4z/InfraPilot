import { useEffect, useMemo, useState } from 'react';
import { format, formatDistanceToNow } from 'date-fns';
import { Check, Search, Trash2, UserX } from 'lucide-react';
import { api, type AdminPersonalAccessToken } from '@/lib/api';
import { TOKEN_ACCESS, TOKEN_STATUS_STYLE, tokenRoleName, tokenStatus, type TokenStatus } from '@/lib/personalTokens';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { inputClass, inputStyle } from './formStyles';

/** What the token secret starts with — how a pasted secret is told apart from a name. */
const SECRET_PREFIX = 'ipt_';

type Pending =
  | { kind: 'token'; token: AdminPersonalAccessToken }
  | { kind: 'owner'; email: string; name: string; active: number };

interface OwnerGroup {
  email: string;
  name: string;
  tokens: AdminPersonalAccessToken[];
  active: number;
}

/**
 * Settings → Agent Tokens: every personal access token people have created on their Agent access
 * page, for an administrator to find and revoke.
 *
 * Built for two moments. A leaked token: paste it into the search, which matches it by its prefix
 * in the browser (the secret is never sent anywhere), and revoke it. Someone leaving or losing
 * their access: revoke all of their tokens at once from their group. Revocations are audit-logged
 * with the reason given here.
 */
export function AgentTokensSettings() {
  const [tokens, setTokens] = useState<AdminPersonalAccessToken[] | null>(null);
  // When the list was fetched — what "expired" is measured against, so rendering stays pure.
  const [loadedAt, setLoadedAt] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [includeInactive, setIncludeInactive] = useState(false);
  // Deliberately not persisted: it may hold a leaked secret.
  const [query, setQuery] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const [pending, setPending] = useState<Pending | null>(null);
  const [busy, setBusy] = useState(false);
  const [revokeError, setRevokeError] = useState<string | null>(null);

  // Bumped after every revoke so the list has one fetch path.
  const [reloadTick, setReloadTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    api
      .listAllTokens(includeInactive)
      .then((r) => {
        if (cancelled) return;
        setTokens(r.tokens ?? []);
        setLoadedAt(Date.now());
        setError(null);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Failed to load tokens');
        setTokens([]);
      });
    return () => {
      cancelled = true;
    };
  }, [includeInactive, reloadTick]);

  const trimmed = query.trim();
  const bySecret = trimmed.startsWith(SECRET_PREFIX);

  // Active tokens per owner over the whole list, not just the search matches: "Revoke all" revokes
  // every one of them, so that is the number it must show.
  const activeByOwner = useMemo(() => {
    const counts = new Map<string, number>();
    for (const t of tokens ?? []) {
      if (tokenStatus(t, loadedAt) === 'active') counts.set(t.ownerEmail, (counts.get(t.ownerEmail) ?? 0) + 1);
    }
    return counts;
  }, [tokens, loadedAt]);

  const groups = useMemo(() => {
    const byOwner = new Map<string, OwnerGroup>();
    for (const t of (tokens ?? []).filter((token) => matchesQuery(token, trimmed))) {
      const group = byOwner.get(t.ownerEmail) ?? {
        email: t.ownerEmail,
        name: t.ownerName,
        tokens: [],
        active: activeByOwner.get(t.ownerEmail) ?? 0,
      };
      group.tokens.push(t);
      byOwner.set(t.ownerEmail, group);
    }
    return [...byOwner.values()].sort((a, b) =>
      (a.name || a.email).localeCompare(b.name || b.email, undefined, { sensitivity: 'base' }),
    );
  }, [tokens, trimmed, activeByOwner]);

  const activeTotal = [...activeByOwner.values()].reduce((sum, n) => sum + n, 0);
  const activeOwners = activeByOwner.size;

  const confirm = async (reason: string) => {
    if (!pending) return;
    setBusy(true);
    setRevokeError(null);
    try {
      if (pending.kind === 'token') {
        await api.adminRevokeToken(pending.token.id, reason.trim());
        setNotice(`Revoked “${pending.token.name}” of ${pending.token.ownerName || pending.token.ownerEmail}.`);
      } else {
        const { revoked } = await api.adminRevokeOwnerTokens(pending.email, reason.trim());
        setNotice(`Revoked ${revoked} ${revoked === 1 ? 'token' : 'tokens'} of ${pending.name || pending.email}.`);
      }
      setPending(null);
      setReloadTick((t) => t + 1);
    } catch (e) {
      setRevokeError(e instanceof Error ? e.message : 'Failed to revoke');
    } finally {
      setBusy(false);
    }
  };

  const ask = (next: Pending) => {
    setRevokeError(null);
    setNotice(null);
    setPending(next);
  };

  return (
    <div className="space-y-4">
      <section
        className="rounded-xl border p-5 space-y-4"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <div>
          <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            Agent tokens
          </h2>
          <p className="text-[13px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Personal access tokens people have created on their Agent access page for AI agents and
            scripts. Each acts as its owner until it expires, is revoked, or the owner's account is
            disabled or deleted in the directory — checked at most every 5 minutes. A standard token has
            no Admin and no approval rights; a <strong>QA access</strong> token also signs off work items,
            and an <strong>Admin access</strong> token keeps Admin and makes its owner's decisions — revoke
            those first when someone's role changes. Only a hash of each secret is stored, so nobody can
            read one here.
          </p>
          <p className="text-[13px] mt-2" style={{ color: 'var(--text-muted)' }}>
            <strong>A token leaked?</strong> Paste it into the search: it is matched by its prefix in your
            browser and never sent to the server. <strong>Someone left or lost access?</strong> Revoke all
            of their tokens at once. Every revoke is recorded in the audit log with the reason you give.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          <label className="relative flex-1 min-w-[220px]">
            <span className="sr-only">Search tokens</span>
            <Search
              size={14}
              className="absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
              style={{ color: 'var(--text-muted)' }}
            />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Person, token name or prefix — or paste a leaked token"
              autoComplete="off"
              spellCheck={false}
              className={`w-full pl-8 ${inputClass}`}
              style={inputStyle}
            />
          </label>
          <label className="inline-flex items-center gap-2 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
            <input
              type="checkbox"
              checked={includeInactive}
              onChange={(e) => setIncludeInactive(e.target.checked)}
            />
            Show revoked and expired (last 90 days)
          </label>
        </div>

        {tokens !== null && (
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {activeTotal} active {activeTotal === 1 ? 'token' : 'tokens'} held by {activeOwners}{' '}
            {activeOwners === 1 ? 'person' : 'people'}
          </p>
        )}

        {notice && (
          <div
            role="status"
            className="flex items-center gap-2 text-[13px] rounded-lg px-3 py-2"
            style={{ color: 'var(--success)', backgroundColor: 'var(--success-bg)' }}
          >
            <Check size={14} className="shrink-0" />
            <span className="flex-1">{notice}</span>
          </div>
        )}

        {error && (
          <div
            role="alert"
            className="text-[13px] rounded-lg px-3 py-2"
            style={{ color: 'var(--danger)', backgroundColor: 'var(--danger-bg)' }}
          >
            {error}
          </div>
        )}

        {tokens === null ? (
          <div className="space-y-2">
            {[1, 2].map((i) => (
              <div key={i} className="skeleton h-14" />
            ))}
          </div>
        ) : groups.length === 0 ? (
          <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
            {!trimmed
              ? includeInactive
                ? 'No tokens in the last 90 days.'
                : 'Nobody holds an active token.'
              : !includeInactive
                ? `No active token matches${bySecret ? ' that secret' : ''}. It may already be revoked or expired — show those to check.`
                : bySecret
                  ? 'No token from the last 90 days matches that secret. It may be older, or not an InfraPortal token.'
                  : 'No token matches the search.'}
          </p>
        ) : (
          <div className="space-y-3">
            {groups.map((group) => (
              <OwnerTokens
                key={group.email}
                group={group}
                loadedAt={loadedAt}
                onRevoke={(token) => ask({ kind: 'token', token })}
                onRevokeAll={() =>
                  ask({ kind: 'owner', email: group.email, name: group.name, active: group.active })
                }
              />
            ))}
          </div>
        )}
      </section>

      {pending && (
        <ConfirmDialog
          title={pending.kind === 'token' ? 'Revoke token?' : 'Revoke all of their tokens?'}
          body={
            pending.kind === 'token' ? (
              <>
                Any agent using <strong>{pending.token.name}</strong> (
                <span className="font-mono">{pending.token.prefix}</span>) of{' '}
                <strong>{pending.token.ownerName || pending.token.ownerEmail}</strong> loses access
                immediately. This can't be undone; they can create a new token themselves.
              </>
            ) : (
              <>
                All {pending.active} active {pending.active === 1 ? 'token' : 'tokens'} of{' '}
                <strong>{pending.name || pending.email}</strong> ({pending.email}) stop working immediately.
                This can't be undone. While their account is active they can still create new ones.
              </>
            )
          }
          confirmLabel={pending.kind === 'token' ? 'Revoke token' : 'Revoke all'}
          confirmTone="danger"
          commentLabel="Reason (optional, recorded in the audit log)"
          busy={busy}
          error={revokeError}
          onConfirm={(reason) => void confirm(reason)}
          onCancel={() => setPending(null)}
        />
      )}
    </div>
  );
}

/**
 * A pasted secret (`ipt_…`) matches the token whose prefix it starts with — compared exactly, since
 * the secret is case-sensitive — and a typed prefix matches the tokens it starts. Anything else
 * matches the owner's name or email or the token's name, ignoring case.
 */
function matchesQuery(token: AdminPersonalAccessToken, query: string): boolean {
  if (!query) return true;
  if (query.startsWith(SECRET_PREFIX)) return query.startsWith(token.prefix) || token.prefix.startsWith(query);
  const q = query.toLowerCase();
  return [token.ownerEmail, token.ownerName, token.name].some((field) => field.toLowerCase().includes(q));
}

function OwnerTokens({
  group,
  loadedAt,
  onRevoke,
  onRevokeAll,
}: {
  group: OwnerGroup;
  loadedAt: number;
  onRevoke: (token: AdminPersonalAccessToken) => void;
  onRevokeAll: () => void;
}) {
  return (
    <div className="rounded-lg border" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}>
      <div
        className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 px-3 py-2.5 border-b"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <div className="min-w-0">
          <div className="text-[13px] font-medium break-words" style={{ color: 'var(--text-primary)' }}>
            {group.name || group.email}
          </div>
          <div className="text-[12px] break-all" style={{ color: 'var(--text-muted)' }}>
            {group.email} · {group.active} active
          </div>
        </div>
        {group.active > 0 && (
          <button
            type="button"
            onClick={onRevokeAll}
            className="shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-colors hover:bg-[var(--danger-bg)]"
            style={{ borderColor: 'var(--border-color)', color: 'var(--danger)' }}
            aria-label={`Revoke all tokens of ${group.name || group.email}`}
          >
            <UserX size={12} />
            Revoke all
          </button>
        )}
      </div>
      <ul>
        {group.tokens.map((token, index) => (
          <TokenRow
            key={token.id}
            token={token}
            status={tokenStatus(token, loadedAt)}
            first={index === 0}
            onRevoke={() => onRevoke(token)}
          />
        ))}
      </ul>
    </div>
  );
}

function TokenRow({
  token,
  status,
  first,
  onRevoke,
}: {
  token: AdminPersonalAccessToken;
  status: TokenStatus;
  first: boolean;
  onRevoke: () => void;
}) {
  const s = TOKEN_STATUS_STYLE[status];
  const accessBadge = TOKEN_ACCESS[token.access]?.badge;
  const inactive = status !== 'active';
  const ended =
    status === 'revoked' && token.revokedAt
      ? `Revoked ${format(new Date(token.revokedAt), 'd MMM yyyy')}`
      : `${status === 'expired' ? 'Expired' : 'Expires'} ${format(new Date(token.expiresAt), 'd MMM yyyy')}`;

  return (
    <li
      className="flex flex-wrap items-start gap-x-4 gap-y-2 px-3 py-2.5"
      style={{ borderTop: first ? undefined : '1px solid var(--border-color)' }}
    >
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span
            className="text-[13px] break-all"
            style={{ color: inactive ? 'var(--text-muted)' : 'var(--text-primary)' }}
          >
            {token.name}
          </span>
          <span
            className="text-[11px] font-semibold px-2 py-0.5 rounded-full"
            style={{ backgroundColor: s.bg, color: s.color }}
          >
            {s.label}
          </span>
          {accessBadge && (
            <span
              className="text-[11px] font-semibold px-2 py-0.5 rounded-full"
              style={{ backgroundColor: accessBadge.bg, color: accessBadge.color }}
              title={TOKEN_ACCESS[token.access].summary}
            >
              {accessBadge.label}
            </span>
          )}
          {token.roles.map((role) => (
            <span
              key={role}
              className="text-[11px] px-1.5 py-0.5 rounded font-medium"
              style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
              title={role}
            >
              {tokenRoleName(role)}
            </span>
          ))}
        </div>
        <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-[12px]" style={{ color: 'var(--text-muted)' }}>
          <span className="font-mono" title="Token prefix — the part that is safe to show">
            {token.prefix}…
          </span>
          <span title={format(new Date(token.createdAt), 'PPpp')}>
            Created {format(new Date(token.createdAt), 'd MMM yyyy')}
          </span>
          <span>{ended}</span>
          <span title={token.lastUsedAt ? format(new Date(token.lastUsedAt), 'PPpp') : undefined}>
            {token.lastUsedAt
              ? `Last used ${formatDistanceToNow(new Date(token.lastUsedAt), { addSuffix: true })}`
              : 'Never used'}
          </span>
        </div>
      </div>
      {status === 'active' && (
        <button
          type="button"
          onClick={onRevoke}
          className="shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-colors hover:bg-[var(--danger-bg)]"
          style={{ borderColor: 'var(--border-color)', color: 'var(--danger)' }}
          aria-label={`Revoke ${token.name} of ${token.ownerName || token.ownerEmail}`}
        >
          <Trash2 size={12} />
          Revoke
        </button>
      )}
    </li>
  );
}
