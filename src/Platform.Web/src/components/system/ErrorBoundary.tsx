import { Component, useEffect, type ErrorInfo, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { reloadApp, useAppUpdateStore } from '@/lib/appUpdate';
import { diagnosticsReport, errorLines, RESET_FOOTNOTE } from '@/lib/diagnostics';
import { getAppName } from '@/lib/runtimeConfig';
import { ActionButton, ProblemCard, ProblemScreen } from './ProblemScreen';

interface FallbackProps {
  error: unknown;
  componentStack: string | null;
  reset: () => void;
}

interface ErrorBoundaryProps {
  children: ReactNode;
  fallback: (props: FallbackProps) => ReactNode;
  /** A change clears a caught error — the pathname, so moving to another page recovers. */
  resetKey?: unknown;
}

interface ErrorBoundaryState {
  hasError: boolean;
  error: unknown;
  componentStack: string | null;
}

/**
 * Without one, any exception thrown while rendering unmounts the whole tree and leaves an empty
 * page. Class-only: React still has no hook for catching render errors.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false, error: null, componentStack: null };

  static getDerivedStateFromError(error: unknown): Partial<ErrorBoundaryState> {
    return { hasError: true, error };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('Render failed:', error, info.componentStack);
    this.setState({ componentStack: info.componentStack ?? null });
  }

  componentDidUpdate(prev: ErrorBoundaryProps) {
    if (this.state.hasError && !Object.is(prev.resetKey, this.props.resetKey)) this.reset();
  }

  reset = () => this.setState({ hasError: false, error: null, componentStack: null });

  render() {
    if (!this.state.hasError) return this.props.children;
    const { error, componentStack } = this.state;
    return this.props.fallback({ error, componentStack, reset: this.reset });
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function ErrorText({ error }: { error: unknown }) {
  return (
    <p className="text-[12px] break-words" style={{ fontFamily: 'var(--font-mono)', color: 'var(--danger)' }}>
      {errorMessage(error)}
    </p>
  );
}

function AppCrash({ error, componentStack }: FallbackProps) {
  const update = useAppUpdateStore((s) => s.available);
  return (
    <ProblemScreen
      title="Something went wrong"
      message={
        <>
          <p>{getAppName()} hit an error it couldn’t recover from.</p>
          <ErrorText error={error} />
          <p>
            {update
              ? 'A newer version has been released since this page loaded — reloading picks it up and will likely fix this.'
              : 'Reloading usually fixes this. If it keeps happening, reset saved data, then copy the details into your report.'}
          </p>
        </>
      }
      details={() => diagnosticsReport('application error', errorLines(error, componentStack))}
      actions={
        <>
          <ActionButton primary onClick={() => reloadApp()}>Reload</ActionButton>
          <ActionButton onClick={() => reloadApp(true)}>Reset saved data and reload</ActionButton>
        </>
      }
      footer={RESET_FOOTNOTE}
    />
  );
}

/**
 * Outermost boundary, around everything including sign-in. Also where the boot watchdog in
 * index.html is told to stand down: this commits on the first render, whether that is the app or
 * the crash screen.
 */
export function AppErrorBoundary({ children }: { children: ReactNode }) {
  useEffect(() => {
    window.__ipBoot?.done();
  }, []);
  return <ErrorBoundary fallback={(props) => <AppCrash {...props} />}>{children}</ErrorBoundary>;
}

function PageCrash({ error, componentStack, reset }: FallbackProps) {
  const update = useAppUpdateStore((s) => s.available);
  return (
    <div className="flex justify-center py-6">
      <ProblemCard
        title="This page failed to load"
        message={
          <>
            <ErrorText error={error} />
            <p>
              {update
                ? 'A newer version has been released since this page loaded, and this page may depend on it. Reload to update.'
                : 'The rest of the app still works — pick another page, try again, or reload.'}
            </p>
          </>
        }
        details={() => diagnosticsReport('page error', errorLines(error, componentStack))}
        actions={
          <>
            <ActionButton primary onClick={() => reloadApp()}>Reload</ActionButton>
            <ActionButton onClick={reset}>Try again</ActionButton>
          </>
        }
      />
    </div>
  );
}

/** Around the routed page only, so one broken page leaves the shell usable. Clears on navigation. */
export function PageErrorBoundary({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  return (
    <ErrorBoundary resetKey={pathname} fallback={(props) => <PageCrash {...props} />}>
      {children}
    </ErrorBoundary>
  );
}
