import type { ReactNode } from 'react';
import { RefreshCw, Sparkles, WifiOff, X } from 'lucide-react';
import { reloadApp, useAppUpdateStore } from '@/lib/appUpdate';
import { describeProblem, problemLines, retryConnectionNow, useConnectionStore } from '@/lib/connection';
import { diagnosticsReport } from '@/lib/diagnostics';
import { ActionButton, CopyDetailsButton } from '@/components/system/ProblemScreen';

/**
 * The shell's two "the app itself has news" strips, above the topbar: the API has stopped
 * answering, or a newer release is live. Both are about the app rather than the page, so they live
 * here instead of on every page's own error state.
 */
export function StatusBanners() {
  return (
    <>
      <ConnectionBanner />
      <UpdateBanner />
    </>
  );
}

function Banner({
  tone,
  icon,
  children,
}: {
  tone: 'danger' | 'info';
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <div
      role={tone === 'danger' ? 'alert' : 'status'}
      className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-2 text-[13px] border-b"
      style={{
        backgroundColor: tone === 'danger' ? 'var(--danger-bg)' : 'var(--info-bg)',
        borderColor: 'var(--border-color)',
        color: 'var(--text-primary)',
      }}
    >
      <span className="shrink-0" style={{ color: tone === 'danger' ? 'var(--danger)' : 'var(--info)' }}>
        {icon}
      </span>
      {children}
    </div>
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
        <button
          type="button"
          onClick={dismiss}
          className="p-1 rounded-md transition-colors hover:bg-[var(--accent-muted)]"
          style={{ color: 'var(--text-muted)' }}
          aria-label="Dismiss until the next release"
        >
          <X size={14} />
        </button>
      </div>
    </Banner>
  );
}
