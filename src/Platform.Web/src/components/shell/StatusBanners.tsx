import type { ReactNode } from 'react';
import { format, formatDistanceToNow } from 'date-fns';
import { KeyRound, RefreshCw, Sparkles, WifiOff, X } from 'lucide-react';
import { reloadApp, useAppUpdateStore } from '@/lib/appUpdate';
import { describeProblem, problemLines, retryConnectionNow, useConnectionStore } from '@/lib/connection';
import { diagnosticsReport } from '@/lib/diagnostics';
import { useDirectoryHealthPolling, useDirectoryHealthStore } from '@/lib/directoryHealth';
import { useAuthStore } from '@/stores/authStore';
import { ActionButton, CopyDetailsButton } from '@/components/system/ProblemScreen';

/**
 * The shell's "the app itself has news" strips, above the topbar: the API has stopped answering,
 * a newer release is live, or (for Admins) the directory app can't sign in. All are about the app
 * rather than the page, so they live here instead of on every page's own error state.
 */
export function StatusBanners() {
  return (
    <>
      <ConnectionBanner />
      <DirectoryHealthBanner />
      <UpdateBanner />
    </>
  );
}

type BannerTone = 'danger' | 'warning' | 'info';

const TONE_COLORS: Record<BannerTone, { background: string; icon: string }> = {
  danger: { background: 'var(--danger-bg)', icon: 'var(--danger)' },
  warning: { background: 'var(--warning-bg)', icon: 'var(--warning)' },
  info: { background: 'var(--info-bg)', icon: 'var(--info)' },
};

function Banner({
  tone,
  icon,
  children,
}: {
  tone: BannerTone;
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <div
      role={tone === 'danger' ? 'alert' : 'status'}
      className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-2 text-[13px] border-b"
      style={{
        backgroundColor: TONE_COLORS[tone].background,
        borderColor: 'var(--border-color)',
        color: 'var(--text-primary)',
      }}
    >
      <span className="shrink-0" style={{ color: TONE_COLORS[tone].icon }}>
        {icon}
      </span>
      {children}
    </div>
  );
}

function DismissButton({ onClick, label }: { onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="p-1 rounded-md transition-colors hover:bg-[var(--accent-muted)]"
      style={{ color: 'var(--text-muted)' }}
      aria-label={label}
    >
      <X size={14} />
    </button>
  );
}

function ConnectionBanner() {
  const problem = useConnectionStore((s) => s.problem);
  if (!problem) return null;

  const { title, message } = describeProblem(problem);
  return (
    <Banner tone="danger" icon={<WifiOff size={15} />}>
      <p className="min-w-0 flex-1">
        <span className="font-semibold">{title}.</span>{' '}
        <span style={{ color: 'var(--text-secondary)' }}>
          {message} Retrying in the background — what's on screen may be out of date.
        </span>
      </p>
      <div className="flex items-center gap-2">
        <ActionButton compact onClick={retryConnectionNow}>
          <RefreshCw size={12} /> Retry now
        </ActionButton>
        <CopyDetailsButton compact details={() => diagnosticsReport('API connection lost', problemLines(problem))} />
      </div>
    </Banner>
  );
}

function UpdateBanner() {
  const available = useAppUpdateStore((s) => s.available);
  const dismissedEntry = useAppUpdateStore((s) => s.dismissedEntry);
  const dismiss = useAppUpdateStore((s) => s.dismiss);
  if (!available || available.entry === dismissedEntry) return null;

  const label = available.version && available.version !== __APP_VERSION__ ? ` (${available.version})` : '';
  return (
    <Banner tone="info" icon={<Sparkles size={15} />}>
      <p className="min-w-0 flex-1">
        <span className="font-semibold">A new version is available{label}.</span>{' '}
        <span style={{ color: 'var(--text-secondary)' }}>
          Reload to update — it also happens by itself the next time you open another page.
        </span>
      </p>
      <div className="flex items-center gap-2">
        <ActionButton compact primary onClick={() => reloadApp()}>
          <RefreshCw size={12} /> Reload now
        </ActionButton>
        <DismissButton onClick={dismiss} label="Dismiss until the next release" />
      </div>
    </Banner>
  );
}

/**
 * Admins only: the API's Graph app can't sign in (an expired client secret, say). Every directory
 * read swallows that on purpose, so without this it only shows as empty person searches and
 * group-based approvals that never match — on 2026-10-19 it went unnoticed for about 20 hours.
 */
function DirectoryHealthBanner() {
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);
  useDirectoryHealthPolling(isAdmin);
  const failure = useDirectoryHealthStore((s) => s.failure);
  const dismissedSince = useDirectoryHealthStore((s) => s.dismissedSince);
  const dismiss = useDirectoryHealthStore((s) => s.dismiss);
  if (!isAdmin || !failure || failure.since === dismissedSince) return null;

  const since = new Date(failure.since);
  const lastFailedAt = new Date(failure.lastFailedAt);
  return (
    <Banner tone="warning" icon={<KeyRound size={15} />}>
      <p className="min-w-0 flex-1">
        <span className="font-semibold">
          Directory (Microsoft Graph) sign-in is failing{failure.code ? `: ${failure.code}` : ''} since{' '}
          <span title={`${formatDistanceToNow(since)} ago; last failed ${format(lastFailedAt, 'PPpp')}`}>
            {format(since, 'd MMM, HH:mm')}
          </span>
          .
        </span>{' '}
        {/* On a phone only the fix: Entra's explanation and the impact are in Copy details. */}
        <span style={{ color: 'var(--text-secondary)' }}>
          <span className="hidden sm:inline">{failure.message} </span>
          Rotate GRAPH_CLIENT_SECRET in ops-infraportal-release.
          <span className="hidden sm:inline">
            {' '}Until then person search comes back empty and group-based approvals can't be checked
            against the directory.
          </span>
        </span>
      </p>
      <div className="flex items-center gap-2">
        <CopyDetailsButton
          compact
          details={() =>
            diagnosticsReport('Directory (Microsoft Graph) sign-in failing', [
              `Code: ${failure.code ?? '(none)'}`,
              `Message: ${failure.message}`,
              `Failing since: ${failure.since}`,
              `Last failed: ${failure.lastFailedAt}`,
              'Fix: rotate GRAPH_CLIENT_SECRET in ops-infraportal-release.',
              'Until then person search comes back empty and group-based approvals cannot be checked against the directory.',
            ])
          }
        />
        <DismissButton onClick={dismiss} label="Dismiss until the next directory outage" />
      </div>
    </Banner>
  );
}
