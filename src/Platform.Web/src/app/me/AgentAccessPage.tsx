import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { format, formatDistanceToNow } from 'date-fns';
import {
  AlertCircle,
  Bot,
  Check,
  Copy,
  KeyRound,
  Plus,
  ShieldCheck,
  Sparkles,
  Trash2,
  X,
  type LucideIcon,
} from 'lucide-react';
import {
  api,
  type CreatedPersonalAccessToken,
  type PersonalAccessToken,
  type TokenAccess,
  type TokenLifetimeDays,
} from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { TOKEN_ACCESS, TOKEN_STATUS_STYLE, tokenRoleName, tokenStatus, type TokenStatus } from '@/lib/personalTokens';
import { getBackendOrigin } from '@/lib/runtimeConfig';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ENV_API_KEY, ENV_URL, TOKEN_PLACEHOLDER, buildAgentSnippets } from './agentSnippets';

/** The server refuses a create beyond this many active tokens; shown so the limit isn't a surprise. */
const MAX_ACTIVE_TOKENS = 10;

const LIFETIMES: TokenLifetimeDays[] = [7, 30, 90, 180, 365];
const DEFAULT_LIFETIME: TokenLifetimeDays = 30;
const DEFAULT_ACCESS: TokenAccess = 'Standard';

const inputClass =
  'px-3 py-2 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]';
const inputStyle = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-secondary)',
  color: 'var(--text-primary)',
};

/**
 * Agent access — the signed-in user's personal access tokens, and how to connect an AI agent to
 * InfraPortal's MCP server with one.
 *
 * Per-user, so it is reachable by everyone from the user menu rather than living under the
 * admin-only Settings. A token's secret is shown exactly once, on creation; until that reveal is
 * dismissed the setup snippets carry it, so the commands can be pasted as they stand.
 *
 * QA members and admins also pick the token's access level (QA, Admin). The server says which ones
 * this user may create, so the picker only appears when there is a choice.
 */
export function AgentAccessPage() {
  useDocumentTitle(['Agent access']);

  const [tokens, setTokens] = useState<PersonalAccessToken[]>([]);
  const [allowedAccess, setAllowedAccess] = useState<TokenAccess[]>([DEFAULT_ACCESS]);
  const [loading, setLoading] = useState(true);
  // When the list was fetched — what "expired" is measured against, so rendering stays pure.
  const [loadedAt, setLoadedAt] = useState(0);
  const [error, setError] = useState<string | null>(null);

  const [name, setName] = useState('');
  const [lifetime, setLifetime] = useState<TokenLifetimeDays>(DEFAULT_LIFETIME);
  const [access, setAccess] = useState<TokenAccess>(DEFAULT_ACCESS);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [created, setCreated] = useState<CreatedPersonalAccessToken | null>(null);

  const [revoking, setRevoking] = useState<PersonalAccessToken | null>(null);
  const [revokeBusy, setRevokeBusy] = useState(false);
  const [revokeError, setRevokeError] = useState<string | null>(null);

  const [reloadTick, setReloadTick] = useState(0);
  const reload = () => setReloadTick((t) => t + 1);

  useEffect(() => {
    let cancelled = false;
    api
      .listMyTokens()
      .then((data) => {
        if (cancelled) return;
        setTokens(data.tokens ?? []);
        setAllowedAccess(data.allowedAccess?.length ? data.allowedAccess : [DEFAULT_ACCESS]);
        setLoadedAt(Date.now());
        setError(null);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load your tokens');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [reloadTick]);

  const activeCount = tokens.filter((t) => tokenStatus(t, loadedAt) === 'active').length;
  const atLimit = activeCount >= MAX_ACTIVE_TOKENS;
  const canCreate = name.trim() !== '' && !creating && !atLimit;

  const handleCreate = async (event: FormEvent) => {
    event.preventDefault();
    if (!canCreate) return;
    setCreating(true);
    setCreateError(null);
    try {
      const result = await api.createMyToken({ name: name.trim(), expiresInDays: lifetime, access });
      setCreated(result);
      setName('');
      setLifetime(DEFAULT_LIFETIME);
      // Back to the least: an elevated token should be a choice made each time, not a leftover.
      setAccess(DEFAULT_ACCESS);
      reload();
    } catch (e) {
      setCreateError(e instanceof Error ? e.message : 'Failed to create the token');
    } finally {
      setCreating(false);
    }
  };

  const confirmRevoke = async () => {
    if (!revoking) return;
    setRevokeBusy(true);
    setRevokeError(null);
    try {
      await api.revokeMyToken(revoking.id);
      // A revoked secret in the snippets would only be pasted into an agent that then can't connect.
      if (created?.id === revoking.id) setCreated(null);
      setRevoking(null);
      reload();
    } catch (e) {
      setRevokeError(e instanceof Error ? e.message : 'Failed to revoke the token');
    } finally {
      setRevokeBusy(false);
    }
  };

  const snippets = buildAgentSnippets({ apiBase: getBackendOrigin(), token: created?.token });

  return (
    <div className="max-w-4xl space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Agent access
        </h1>
        <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
          Personal access tokens let AI agents such as Claude Code work in InfraPortal as you, through
          its MCP server or the REST API.
        </p>
      </div>

      {error && (
        <div
          role="alert"
          className="flex items-center gap-2 px-4 py-3 rounded-lg border text-[13px]"
          style={{ backgroundColor: 'var(--danger-bg)', borderColor: 'var(--danger)', color: 'var(--danger)' }}
        >
          <AlertCircle size={15} className="shrink-0" />
          <span className="flex-1">{error}</span>
          <button type="button" onClick={() => setError(null)} aria-label="Dismiss error">
            <X size={14} />
          </button>
        </div>
      )}

      {created && <CreatedTokenReveal token={created} onDismiss={() => setCreated(null)} />}

      <Panel
        icon={KeyRound}
        title="Personal access tokens"
        aside={
          loading ? null : (
            <span className="text-[12px]" style={{ color: atLimit ? 'var(--warning)' : 'var(--text-muted)' }}>
              {activeCount} of {MAX_ACTIVE_TOKENS} active
            </span>
          )
        }
      >
        <form onSubmit={(e) => void handleCreate(e)} className="flex flex-wrap items-end gap-3">
          <label className="flex-1 min-w-[200px] space-y-1.5">
            <span className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Name
            </span>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Claude Code on my laptop"
              disabled={atLimit}
              className={`w-full ${inputClass}`}
              style={inputStyle}
            />
          </label>
          <label className="space-y-1.5">
            <span className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
              Expires after
            </span>
            <select
              value={lifetime}
              onChange={(e) => setLifetime(Number(e.target.value) as TokenLifetimeDays)}
              disabled={atLimit}
              className={inputClass}
              style={inputStyle}
            >
              {LIFETIMES.map((days) => (
                <option key={days} value={days}>
                  {days === 365 ? '1 year' : `${days} days`}
                </option>
              ))}
            </select>
          </label>
          {allowedAccess.length > 1 && (
            <label className="space-y-1.5">
              <span className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
                Access
              </span>
              <select
                value={access}
                onChange={(e) => setAccess(e.target.value as TokenAccess)}
                disabled={atLimit}
                className={inputClass}
                style={inputStyle}
              >
                {allowedAccess.map((level) => (
                  <option key={level} value={level}>
                    {TOKEN_ACCESS[level].label}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button
            type="submit"
            disabled={!canCreate}
            className="inline-flex items-center gap-1.5 text-[13px] font-medium px-4 py-2 rounded-lg text-white transition-opacity hover:opacity-90 disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            <Plus size={15} />
            {creating ? 'Creating…' : 'Create token'}
          </button>
        </form>

        {allowedAccess.length > 1 && (
          <p
            className="text-[12px]"
            style={{ color: access === 'Admin' ? 'var(--warning)' : 'var(--text-muted)' }}
          >
            <strong>{TOKEN_ACCESS[access].label}:</strong> {TOKEN_ACCESS[access].summary}
            {access !== 'Standard' && ' Give it only to an agent you trust with that, for no longer than it needs.'}
          </p>
        )}
        {atLimit && (
          <p className="text-[12px]" style={{ color: 'var(--warning)' }}>
            You have {MAX_ACTIVE_TOKENS} active tokens. Revoke one to create another.
          </p>
        )}
        {createError && (
          <p role="alert" className="text-[12px]" style={{ color: 'var(--danger)' }}>
            {createError}
          </p>
        )}

        {loading ? (
          <div className="space-y-2">
            {[1, 2].map((i) => (
              <div key={i} className="skeleton h-14" />
            ))}
          </div>
        ) : tokens.length === 0 ? (
          <p className="text-[12px] py-2" style={{ color: 'var(--text-muted)' }}>
            No tokens yet. Create one to connect an agent.
          </p>
        ) : (
          <ul className="rounded-lg border" style={{ borderColor: 'var(--border-color)' }}>
            {tokens.map((token, index) => (
              <TokenRow
                key={token.id}
                token={token}
                status={tokenStatus(token, loadedAt)}
                first={index === 0}
                onRevoke={() => {
                  setRevokeError(null);
                  setRevoking(token);
                }}
              />
            ))}
          </ul>
        )}
      </Panel>

      <Panel icon={ShieldCheck} title="How a token works">
        <ul className="space-y-2 text-[13px] list-disc pl-5" style={{ color: 'var(--text-secondary)' }}>
          <li>
            <strong style={{ color: 'var(--text-primary)' }}>It acts as you.</strong> Comments and actions
            carry your name and email, with the roles you hold when you create it — except Admin, unless
            it is an Admin token.
          </li>
          <li>
            <strong style={{ color: 'var(--text-primary)' }}>No pipeline-only actions.</strong> It can't
            ingest deploy events, register builds or create promotions through the pipeline endpoint.
          </li>
          <li>
            <strong style={{ color: 'var(--text-primary)' }}>Decisions only if you allow them.</strong> A
            standard token can't approve, reject or sign off promotions, work items or rollbacks — you make
            those calls in the portal. QA members can create a <strong>QA</strong> token, which also signs off
            work items; admins can create an <strong>Admin</strong> token, which keeps Admin and makes every
            decision you can. Each decision a token makes is recorded in the audit log as made by an agent.
          </li>
          <li>
            <strong style={{ color: 'var(--text-primary)' }}>It expires</strong> after 7, 30, 90 or 180 days
            or a year, and you can revoke it here at any time. An administrator can revoke it too, and it stops working
            within minutes if your account is disabled. Treat it like a password.
          </li>
          <li>
            <strong style={{ color: 'var(--text-primary)' }}>One token, MCP and REST.</strong> It's sent as the{' '}
            <InlineCode>X-Api-Key</InlineCode> header — the same one pipeline API keys use — so it works for
            the MCP server and the REST API alike.
          </li>
        </ul>
      </Panel>

      <Panel icon={Bot} title="Connect an agent">
        <div className="space-y-1.5">
          <span className="block text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            MCP server URL
          </span>
          <div className="flex items-center gap-2">
            <code
              className="flex-1 min-w-0 truncate text-[12px] px-3 py-2 rounded-lg font-mono border"
              style={{ ...inputStyle }}
            >
              {snippets.mcpUrl}
            </code>
            <CopyButton text={snippets.mcpUrl} ariaLabel="Copy MCP server URL" />
          </div>
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {created ? (
              <>The snippets below include the token you just created.</>
            ) : (
              <>
                Replace <InlineCode>{TOKEN_PLACEHOLDER}</InlineCode> with a token created above.
              </>
            )}
          </p>
        </div>

        <Step n={1} title="Claude Code" badge="Recommended">
          <p>Adds the server for your user, in every project. The token is saved in your Claude Code config.</p>
          <CodeBlock label="Terminal" code={snippets.claudeCode} />
          <p>
            Then run <InlineCode>/mcp</InlineCode> inside Claude Code — <InlineCode>infraportal</InlineCode>{' '}
            should be listed as connected.
          </p>
        </Step>

        <Step n={2} title="Project .mcp.json — shareable, no secret in the file">
          <p>
            Commit this to a repository so everyone gets the server; each person's token comes from their
            own environment.
          </p>
          <CodeBlock label=".mcp.json" code={snippets.projectMcpJson} />
          <p>
            Then set <InlineCode>{ENV_URL}</InlineCode> and <InlineCode>{ENV_API_KEY}</InlineCode> — the
            same two variables the repo's <InlineCode>infrapilot-api</InlineCode> skill and scripts use for
            REST calls.
          </p>
          <CodeBlock label="PowerShell" code={snippets.envPowerShell} />
          <CodeBlock label="bash / zsh" code={snippets.envBash} />
        </Step>

        <Step n={3} title="VS Code (GitHub Copilot agent mode)">
          <p>
            Save as <InlineCode>.vscode/mcp.json</InlineCode>. VS Code asks for the token when the server
            first starts and keeps it in its secret storage.
          </p>
          <CodeBlock label=".vscode/mcp.json" code={snippets.vscodeMcpJson} />
        </Step>
      </Panel>

      <Panel icon={Sparkles} title="What the agent can do">
        <ul className="space-y-1.5 text-[13px] list-disc pl-5" style={{ color: 'var(--text-secondary)' }}>
          <li>Read deployment state, history, events and logs, promotions, builds and analytics</li>
          <li>Diagnose stuck promotions and failed deployments</li>
          <li>Search platform knowledge</li>
          <li>Comment on promotions and work items</li>
          <li>Manage promotion participants</li>
          <li>Promote a registered build</li>
        </ul>
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          The MCP tools never approve or reject. With a QA or Admin token an agent can make those decisions
          through the REST API; with a standard token they stay yours, made here in the portal.
        </p>
      </Panel>

      {revoking && (
        <ConfirmDialog
          title="Revoke token?"
          body={
            <>
              Any agent using <strong>{revoking.name}</strong> (
              <span className="font-mono">{revoking.prefix}</span>) loses access immediately. This can't be
              undone.
            </>
          }
          confirmLabel="Revoke token"
          confirmTone="danger"
          busy={revokeBusy}
          error={revokeError}
          onConfirm={() => void confirmRevoke()}
          onCancel={() => setRevoking(null)}
        />
      )}
    </div>
  );
}

/**
 * The one-time reveal. Styled as a warning and kept on screen until dismissed, because once it goes
 * the secret is gone for good — the server only ever stores its hash.
 */
function CreatedTokenReveal({
  token,
  onDismiss,
}: {
  token: CreatedPersonalAccessToken;
  onDismiss: () => void;
}) {
  const [copied, setCopied] = useState(false);

  return (
    <div
      role="status"
      className="rounded-xl border p-4 space-y-3"
      style={{ borderColor: 'var(--warning)', backgroundColor: 'var(--warning-bg)' }}
    >
      <div>
        <p className="text-[13px] font-semibold" style={{ color: 'var(--warning)' }}>
          Copy your new token now — it won't be shown again
        </p>
        <p className="text-[12px] mt-0.5" style={{ color: 'var(--text-secondary)' }}>
          <strong>{token.name}</strong> · expires {format(new Date(token.expiresAt), 'd MMM yyyy')}. The
          setup snippets below include it until you dismiss this.
        </p>
      </div>
      <div className="flex items-center gap-2">
        <code
          className="flex-1 min-w-0 break-all text-[13px] px-3 py-2 rounded-lg font-mono"
          style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
        >
          {token.token}
        </code>
        <CopyButton text={token.token} ariaLabel="Copy token" onCopied={() => setCopied(true)} />
      </div>
      <div className="flex justify-end">
        <button
          type="button"
          onClick={onDismiss}
          className="text-[12px] font-medium px-3 py-1.5 rounded-lg transition-opacity hover:opacity-80"
          style={
            copied
              ? { backgroundColor: 'var(--warning-solid)', color: 'white' }
              : { backgroundColor: 'var(--bg-primary)', color: 'var(--text-secondary)' }
          }
        >
          {copied ? "I've saved it — hide token" : 'Hide token'}
        </button>
      </div>
    </div>
  );
}

function TokenRow({
  token,
  status,
  first,
  onRevoke,
}: {
  token: PersonalAccessToken;
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
      className="flex flex-wrap items-start gap-x-4 gap-y-2 px-3 py-3"
      style={{ borderTop: first ? undefined : '1px solid var(--border-color)' }}
    >
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span
            className="text-[13px] font-medium break-all"
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
        {token.roles.length > 0 && (
          <div className="mt-1.5 flex flex-wrap gap-1">
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
        )}
      </div>
      {status === 'active' && (
        <button
          type="button"
          onClick={onRevoke}
          className="shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-colors hover:bg-[var(--danger-bg)]"
          style={{ borderColor: 'var(--border-color)', color: 'var(--danger)' }}
          aria-label={`Revoke ${token.name}`}
        >
          <Trash2 size={12} />
          Revoke
        </button>
      )}
    </li>
  );
}

/** A titled panel: header bar over the content, the framing the My tasks sections use. */
function Panel({
  icon: Icon,
  title,
  aside,
  children,
}: {
  icon: LucideIcon;
  title: string;
  aside?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section
      className="rounded-xl border overflow-hidden"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div
        className="flex items-center justify-between gap-3 px-4 py-3 border-b"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <h2
          className="flex items-center gap-2.5 text-[13px] font-semibold min-w-0"
          style={{ color: 'var(--text-primary)' }}
        >
          <span
            className="shrink-0 w-7 h-7 rounded-lg inline-flex items-center justify-center"
            style={{ backgroundColor: 'var(--accent-bg)', color: 'var(--accent)' }}
          >
            <Icon size={15} />
          </span>
          <span className="truncate">{title}</span>
        </h2>
        {aside}
      </div>
      <div className="p-4 space-y-4">{children}</div>
    </section>
  );
}

function Step({
  n,
  title,
  badge,
  children,
}: {
  n: number;
  title: string;
  badge?: string;
  children: ReactNode;
}) {
  return (
    <div className="flex gap-3">
      <span
        className="shrink-0 w-6 h-6 rounded-full inline-flex items-center justify-center text-[12px] font-semibold"
        style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
      >
        {n}
      </span>
      <div className="min-w-0 flex-1 space-y-2 text-[13px]" style={{ color: 'var(--text-secondary)' }}>
        <h3 className="flex flex-wrap items-center gap-2 text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          {title}
          {badge && (
            <span
              className="text-[11px] font-semibold px-2 py-0.5 rounded-full"
              style={{ backgroundColor: 'var(--success-bg)', color: 'var(--success)' }}
            >
              {badge}
            </span>
          )}
        </h3>
        {children}
      </div>
    </div>
  );
}

function CodeBlock({ label, code }: { label: string; code: string }) {
  return (
    <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--border-color)' }}>
      <div
        className="flex items-center justify-between gap-2 px-3 py-1.5 border-b"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
      >
        <span className="text-[11px] font-medium font-mono" style={{ color: 'var(--text-muted)' }}>
          {label}
        </span>
        <CopyButton text={code} ariaLabel={`Copy ${label} snippet`} compact />
      </div>
      <pre
        className="p-3 text-[12px] leading-relaxed overflow-x-auto font-mono"
        style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
      >
        {code}
      </pre>
    </div>
  );
}

function InlineCode({ children }: { children: ReactNode }) {
  return (
    <code
      className="text-[12px] px-1.5 py-0.5 rounded font-mono"
      style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--accent)' }}
    >
      {children}
    </code>
  );
}

/**
 * Clipboard write with the `execCommand` fallback, since the async API is missing outside a secure
 * context — a portal reached over plain http on an internal host name.
 */
async function copyToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
    const area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(area);
    return ok;
  } catch {
    return false;
  }
}

function CopyButton({
  text,
  ariaLabel,
  compact = false,
  onCopied,
}: {
  text: string;
  ariaLabel: string;
  /** Smaller, borderless — for the header bar of a code block. */
  compact?: boolean;
  onCopied?: () => void;
}) {
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    if (!(await copyToClipboard(text))) return;
    setCopied(true);
    onCopied?.();
    window.setTimeout(() => setCopied(false), 2000);
  };

  return (
    <button
      type="button"
      onClick={() => void copy()}
      aria-label={copied ? 'Copied' : ariaLabel}
      className={
        compact
          ? 'shrink-0 inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] font-medium transition-opacity hover:opacity-80'
          : 'shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-opacity hover:opacity-80'
      }
      style={{
        borderColor: copied ? 'var(--success)' : 'var(--border-color)',
        backgroundColor: compact ? 'transparent' : 'var(--bg-primary)',
        color: copied ? 'var(--success)' : 'var(--text-secondary)',
      }}
    >
      {copied ? <Check size={12} /> : <Copy size={12} />}
      {copied ? 'Copied' : 'Copy'}
    </button>
  );
}
