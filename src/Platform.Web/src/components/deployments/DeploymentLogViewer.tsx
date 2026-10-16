import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Copy,
  Check,
  Download,
  Loader2,
  ScrollText,
  Scissors,
} from 'lucide-react';
import { api } from '@/lib/api';
import { classifyLog, formatLogSize, type ClassifiedLine, type LogLineKind } from '@/lib/deployLog';
import type { DeployLogSummary } from '@/lib/types';

/**
 * Viewer for the output a deploy pipeline captured — the Helm release printout and its failure
 * diagnostics.
 *
 * Each block is collapsed until opened and fetches its own text at that point: the detail response
 * carries only names and sizes, because a release log runs to hundreds of kilobytes and most visits
 * never expand one. A block the caller marks `defaultOpen` (the page does this for failed
 * deployments) fetches immediately, since there the log *is* the reason for the visit.
 *
 * Error and warning lines are coloured and counted, with a jump straight to the first error, because
 * the line that explains a failure is otherwise buried in routine chatter.
 */

const LINE_STYLES: Record<LogLineKind, { color: string; background?: string; weight?: number }> = {
  error: { color: 'var(--danger)', background: 'var(--danger-bg)', weight: 500 },
  warning: { color: 'var(--warning)', background: 'var(--warning-bg)' },
  section: { color: 'var(--accent)', weight: 600 },
  normal: { color: 'var(--text-secondary)' },
};

interface Props {
  eventId: string;
  logs: DeployLogSummary[];
  /** Open (and fetch) every block on mount. The page sets this when the deployment failed. */
  defaultOpen?: boolean;
}

export function DeploymentLogViewer({ eventId, logs, defaultOpen = false }: Props) {
  if (logs.length === 0) {
    return (
      <p className="text-[13px]" style={{ color: 'var(--text-muted)' }}>
        The pipeline that ran this deployment didn't send any output. Newer deployments carry the Helm
        release printout and, when they fail, the diagnostics collected at the time.
      </p>
    );
  }

  return (
    <div className="space-y-2">
      {logs.map((log) => (
        <LogBlock key={log.id} eventId={eventId} log={log} defaultOpen={defaultOpen} />
      ))}
    </div>
  );
}

function LogBlock({ eventId, log, defaultOpen }: { eventId: string; log: DeployLogSummary; defaultOpen: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const [content, setContent] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const bodyRef = useRef<HTMLDivElement>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const fetched = await api.getDeploymentLog(eventId, log.id);
      setContent(fetched.content);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load this log');
    } finally {
      setLoading(false);
    }
  }, [eventId, log.id]);

  // Fetch on first open only — a re-collapse keeps what it already has, since a finished
  // deployment's log never changes.
  useEffect(() => {
    if (open && content === null && !loading && error === null) void load();
  }, [open, content, loading, error, load]);

  const classified = useMemo(() => (content === null ? null : classifyLog(content)), [content]);

  const copy = useCallback(async () => {
    if (content === null) return;
    await navigator.clipboard.writeText(content);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }, [content]);

  const download = useCallback(() => {
    if (content === null) return;
    const blob = new Blob([content], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    // Spaces and slashes in a block name would make an awkward filename.
    a.download = `${log.name.replace(/[^a-z0-9]+/gi, '-').toLowerCase()}.log`;
    a.click();
    URL.revokeObjectURL(url);
  }, [content, log.name]);

  const jumpToFirstError = useCallback(() => {
    if (classified?.firstErrorLine == null) return;
    bodyRef.current
      ?.querySelector(`[data-log-line="${classified.firstErrorLine}"]`)
      ?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }, [classified]);

  return (
    <div
      className="rounded-lg border overflow-hidden"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-center gap-2 px-3 py-2">
        <button
          onClick={() => setOpen((v) => !v)}
          className="flex items-center gap-2 min-w-0 flex-1 text-left transition-opacity hover:opacity-80"
          aria-expanded={open}
        >
          {open ? <ChevronDown size={14} style={{ color: 'var(--text-muted)' }} /> : <ChevronRight size={14} style={{ color: 'var(--text-muted)' }} />}
          <ScrollText size={13} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
          <span className="text-[13px] font-medium truncate" style={{ color: 'var(--text-primary)' }}>
            {log.name}
          </span>
          {log.source && (
            <span className="badge text-[10px] shrink-0" style={{ backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-muted)' }}>
              {log.source}
            </span>
          )}
          {/* Counted from the fetched text, so this only appears once a block has been opened. */}
          {classified && classified.errorCount > 0 && (
            <span
              className="badge text-[10px] shrink-0"
              style={{ backgroundColor: 'var(--danger-bg)', color: 'var(--danger)' }}
            >
              <AlertTriangle size={9} />
              {classified.errorCount} {classified.errorCount === 1 ? 'error' : 'errors'}
            </span>
          )}
        </button>

        <span className="text-[11px] whitespace-nowrap shrink-0" style={{ color: 'var(--text-muted)' }}>
          {log.lineCount.toLocaleString()} lines · {formatLogSize(log.byteCount)}
        </span>

        {open && content !== null && (
          <div className="flex items-center gap-1 shrink-0">
            {classified?.firstErrorLine != null && (
              <button
                onClick={jumpToFirstError}
                title={`Jump to the first error (line ${classified.firstErrorLine})`}
                className="p-1 rounded transition-opacity hover:opacity-70"
                style={{ color: 'var(--danger)' }}
              >
                <AlertTriangle size={13} />
              </button>
            )}
            <button
              onClick={copy}
              title={copied ? 'Copied' : 'Copy the whole log'}
              className="p-1 rounded transition-opacity hover:opacity-70"
              style={{ color: copied ? 'var(--success)' : 'var(--text-muted)' }}
            >
              {copied ? <Check size={13} /> : <Copy size={13} />}
            </button>
            <button
              onClick={download}
              title="Download as a .log file"
              className="p-1 rounded transition-opacity hover:opacity-70"
              style={{ color: 'var(--text-muted)' }}
            >
              <Download size={13} />
            </button>
          </div>
        )}
      </div>

      {open && (
        <div className="border-t" style={{ borderColor: 'var(--border-color)' }}>
          {log.truncated && (
            <div
              className="flex items-center gap-2 px-3 py-1.5 text-[11px]"
              style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
            >
              <Scissors size={11} />
              Only the end of this log was kept — a deploy prints its diagnostics last, so the tail is
              what survives the size cap. The full output is in the pipeline run.
            </div>
          )}
          {loading && (
            <div className="flex items-center gap-2 px-3 py-4 text-[12px]" style={{ color: 'var(--text-muted)' }}>
              <Loader2 className="animate-spin" size={13} /> Loading log…
            </div>
          )}
          {error && (
            <div className="flex items-center justify-between gap-2 px-3 py-3 text-[12px]" style={{ color: 'var(--danger)' }}>
              <span>{error}</span>
              <button
                onClick={() => { setError(null); void load(); }}
                className="font-medium transition-opacity hover:opacity-80"
                style={{ color: 'var(--accent)' }}
              >
                Retry
              </button>
            </div>
          )}
          {classified && (
            <div
              ref={bodyRef}
              className="max-h-[480px] overflow-auto font-mono text-[11.5px] leading-[1.6] py-1.5"
              style={{ backgroundColor: 'var(--bg-primary)' }}
            >
              {classified.lines.length === 1 && classified.lines[0].text === '' ? (
                <p className="px-3 py-2 text-[12px] font-sans" style={{ color: 'var(--text-muted)' }}>
                  This block was captured empty.
                </p>
              ) : (
                classified.lines.map((line) => <LogLine key={line.number} line={line} />)
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function LogLine({ line }: { line: ClassifiedLine }) {
  const style = LINE_STYLES[line.kind];
  return (
    <div
      data-log-line={line.number}
      className="flex gap-3 px-3"
      style={{ backgroundColor: style.background }}
    >
      <span
        className="select-none text-right shrink-0 tabular-nums"
        style={{ color: 'var(--text-muted)', opacity: 0.6, minWidth: '3.5ch' }}
      >
        {line.number}
      </span>
      {/* pre-wrap so long kubectl lines wrap instead of forcing a horizontal scroll on the page. */}
      <span className="whitespace-pre-wrap break-words" style={{ color: style.color, fontWeight: style.weight }}>
        {line.text || ' '}
      </span>
    </div>
  );
}
