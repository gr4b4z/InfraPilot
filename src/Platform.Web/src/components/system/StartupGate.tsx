import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Loader2 } from 'lucide-react';
import { loadAuthConfig } from '@/lib/authConfig';
import { describeProblem, problemLines, type ConnectionProblem } from '@/lib/connection';
import { reloadApp } from '@/lib/appUpdate';
import { diagnosticsReport } from '@/lib/diagnostics';
import { ActionButton, ProblemScreen } from './ProblemScreen';

/** Auto-retry backoff, in seconds; the last value repeats. */
const RETRY_DELAYS_S = [5, 10, 20, 30];
/** A fast start shouldn't flash a spinner. */
const SPINNER_DELAY_MS = 400;

/**
 * Holds the app back until the API has said how sign-in works — nothing below can render
 * correctly without it. When the API can't be reached (after `loadAuthConfig`'s own quick retries),
 * says so and why, and keeps retrying with a visible countdown instead of a bare "try again".
 * Dev never gets here: there `loadAuthConfig` falls back to no auth so the shell loads without a
 * backend.
 */
export function StartupGate({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const [problem, setProblem] = useState<ConnectionProblem | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [checking, setChecking] = useState(true);

  useEffect(() => {
    let cancelled = false;
    void loadAuthConfig().then((found) => {
      if (cancelled) return;
      setChecking(false);
      setProblem(found);
      if (!found) setReady(true);
    });
    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const retry = useCallback(() => {
    setChecking(true);
    setAttempt((a) => a + 1);
  }, []);

  if (ready) return <>{children}</>;
  if (!problem) return <Connecting />;
  return (
    <Unreachable
      // A fresh failure restarts the countdown.
      key={problem.at}
      problem={problem}
      attempt={attempt}
      checking={checking}
      onRetry={retry}
    />
  );
}

function Connecting() {
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setVisible(true), SPINNER_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, []);
  return (
    <div
      className="flex flex-col items-center justify-center h-screen gap-3"
      style={{ backgroundColor: 'var(--bg-primary)', visibility: visible ? 'visible' : 'hidden' }}
    >
      <Loader2 size={24} className="animate-spin" style={{ color: 'var(--accent)' }} />
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>Connecting…</p>
    </div>
  );
}

function Unreachable({
  problem,
  attempt,
  checking,
  onRetry,
}: {
  problem: ConnectionProblem;
  attempt: number;
  checking: boolean;
  onRetry: () => void;
}) {
  const [secondsLeft, setSecondsLeft] = useState(
    RETRY_DELAYS_S[Math.min(attempt, RETRY_DELAYS_S.length - 1)],
  );

  // The clock only runs between attempts, not while one is in flight.
  useEffect(() => {
    if (checking) return;
    const timer = window.setInterval(() => setSecondsLeft((s) => s - 1), 1000);
    return () => window.clearInterval(timer);
  }, [checking]);

  useEffect(() => {
    if (!checking && secondsLeft <= 0) onRetry();
  }, [checking, secondsLeft, onRetry]);

  // Back online is the moment most likely to succeed — don't sit out the countdown.
  useEffect(() => {
    window.addEventListener('online', onRetry);
    return () => window.removeEventListener('online', onRetry);
  }, [onRetry]);

  const { title, message } = describeProblem(problem);
  return (
    <ProblemScreen
      title={title}
      message={
        <>
          <p>{message}</p>
          <p style={{ color: 'var(--text-muted)' }}>
            {checking ? 'Trying again…' : `Trying again in ${Math.max(secondsLeft, 0)} s`}
            {attempt > 0 && ` · ${attempt + 1} attempts so far`}
          </p>
        </>
      }
      details={() =>
        diagnosticsReport('cannot reach the API', [
          ...problemLines(problem),
          `Attempts: ${attempt + 1}`,
        ])
      }
      actions={
        <>
          <ActionButton primary onClick={onRetry} disabled={checking}>Retry now</ActionButton>
          <ActionButton onClick={() => reloadApp()}>Reload page</ActionButton>
        </>
      }
      footer="If this persists, copy the details and send them along with your report — they say which request failed and how."
    />
  );
}
