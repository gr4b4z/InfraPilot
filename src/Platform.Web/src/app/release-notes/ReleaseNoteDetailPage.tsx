import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, Check, Copy, Loader2 } from 'lucide-react';
import { marked } from 'marked';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { RovingGroup } from '@/components/ui/RovingGroup';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import { api, type ReleaseNoteDetail } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';

// Render markdown synchronously; content originates from our own server-side
// template engine so we don't sanitize further here.
marked.setOptions({ gfm: true, breaks: false });

export function ReleaseNoteDetailPage() {
  const { product = '', id = '' } = useParams<{ product: string; id: string }>();
  const [note, setNote] = useState<ReleaseNoteDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<'rendered' | 'services'>('rendered');
  const [copied, setCopied] = useState(false);

  async function copyMarkdown() {
    if (!note) return;
    try {
      await navigator.clipboard.writeText(note.renderedContent);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard API can fail in iframes / non-HTTPS — fall back to a manual select.
      const ta = document.createElement('textarea');
      ta.value = note.renderedContent;
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); setCopied(true); setTimeout(() => setCopied(false), 1500); } catch { /* noop */ }
      ta.remove();
    }
  }

  // Convert stored markdown to HTML once per note. marked.parse is sync when no
  // async extensions are registered, so the cast is safe.
  const html = useMemo(
    () => (note ? (marked.parse(note.renderedContent) as string) : ''),
    [note]
  );

  useEffect(() => {
    let cancelled = false;
    api.getReleaseNote(id)
      .then((n) => { if (!cancelled) setNote(n); })
      .catch((e) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); });
    return () => { cancelled = true; };
  }, [id]);

  // Above the early returns below, so the hook order is stable. The product comes from the route, so
  // it's there before the note loads; the environment arrives with it.
  useDocumentTitle([note?.product ?? product, note?.environment, 'Release notes']);

  if (error) {
    return (
      <div className="px-3 py-2 rounded-lg text-[13px]" style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}>
        {error}
      </div>
    );
  }
  if (!note) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <Link
        to={`/release-notes/${product}`}
        className="inline-flex items-center gap-1 text-[12px]"
        style={{ color: 'var(--text-muted)' }}
      >
        <ArrowLeft size={14} /> Back to release notes
      </Link>

      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {note.product} — {note.environment}
          </h1>
          <p className="text-[12px] mt-1 font-mono" style={{ color: 'var(--text-muted)' }}>
            {new Date(note.from).toLocaleString()} → {new Date(note.to).toLocaleString()}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={copyMarkdown}
            className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-md border text-[12px]"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
            title="Copy rendered markdown to clipboard"
          >
            {copied ? <Check size={12} /> : <Copy size={12} />}
            {copied ? 'Copied' : 'Copy markdown'}
          </button>
          <RovingGroup
            ariaLabel="Release note view"
            className="inline-flex items-center rounded-lg overflow-hidden border"
            style={{ borderColor: 'var(--border-color)' }}
          >
            {(['rendered', 'services'] as const).map((v) => (
              <button
                key={v}
                onClick={() => setView(v)}
                aria-pressed={view === v}
                className="px-3 py-1.5 text-[12px] capitalize"
                style={{
                  backgroundColor: view === v ? 'var(--accent-subtle)' : 'transparent',
                  color: view === v ? 'var(--accent)' : 'var(--text-secondary)',
                }}
              >{v}</button>
            ))}
          </RovingGroup>
        </div>
      </div>

      {view === 'rendered' ? (
        <div
          className="rounded-xl border p-6"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
        >
          {/* Card spans the page, but the prose keeps a reading width — full-viewport
              line lengths are unreadable for long-form notes. */}
          <div
            className="release-notes-prose text-[14px] overflow-x-auto max-w-[80ch]"
            style={{ color: 'var(--text-primary)' }}
            dangerouslySetInnerHTML={{ __html: html }}
          />
        </div>
      ) : (
        <KeyboardList
          className="space-y-3"
          count={note.raw.services.length}
          ariaLabel="Services in this release note"
          // These blocks are the content, not links to it — the work-item and pull-request links
          // inside them have to stay tabbable.
          sweepNestedTabStops={false}
        >
          {note.raw.services.map((svc, index) => (
            <ServiceBlock
              key={svc.service}
              index={index}
              label={`${svc.service}, ${svc.previousVersion ?? 'none'} to ${svc.currentVersion}`}
            >
              <div className="flex items-baseline justify-between gap-3">
                <h3 className="font-semibold text-[14px]" style={{ color: 'var(--text-primary)' }}>{svc.service}</h3>
                <span className="text-[12px] font-mono" style={{ color: 'var(--text-muted)' }}>
                  {svc.previousVersion ?? '—'} → {svc.currentVersion}
                  {svc.isRollback && ' ⚠ rollback'}
                </span>
              </div>
              {svc.workItems.length > 0 && (
                <div className="mt-2">
                  <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Work items</div>
                  <ul className="mt-1 text-[13px] space-y-0.5">
                    {svc.workItems.map((w) => (
                      <li key={w.key} style={{ color: 'var(--text-secondary)' }}>
                        {w.url ? <a href={w.url} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)' }}>[{w.key}]</a> : <span>[{w.key}]</span>} {w.title}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {svc.pullRequests.length > 0 && (
                <div className="mt-2">
                  <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Pull requests</div>
                  <ul className="mt-1 text-[13px] space-y-0.5">
                    {svc.pullRequests.map((p, i) => (
                      <li key={`${p.key}-${i}`} style={{ color: 'var(--text-secondary)' }}>
                        {p.url ? <a href={p.url} target="_blank" rel="noreferrer" style={{ color: 'var(--accent)' }}>{p.key ?? p.url}</a> : <span>{p.key}</span>} {p.title}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {svc.participants.length > 0 && (
                <div className="mt-2">
                  <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>Participants</div>
                  <ul className="mt-1 text-[13px] space-y-0.5">
                    {svc.participants.map((p, i) => (
                      <li key={`${p.role}-${i}`} style={{ color: 'var(--text-secondary)' }}>
                        <span className="font-mono text-[11px]" style={{ color: 'var(--text-muted)' }}>{p.role}</span>{' '}
                        {p.displayName ?? p.email ?? '—'}
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </ServiceBlock>
          ))}
        </KeyboardList>
      )}
    </div>
  );
}

/**
 * One service's entry in a release note's services view.
 *
 * A focusable wrapper rather than a link: the block has nothing to open — it *is* the content, a
 * breakdown of work items, pull requests and people. So the arrow keys move between services and
 * Enter does nothing, which is why no `onActivate` is supplied.
 */
function ServiceBlock({
  index,
  label,
  children,
}: {
  index: number;
  label: string;
  children: React.ReactNode;
}) {
  const rowProps = useKeyboardListRow(index, () => {}, {
    role: null,
    label,
    // Nothing to open; without this the no-op handler would swallow Enter.
    selfActivating: true,
  });
  return (
    <div
      {...rowProps}
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      {children}
    </div>
  );
}
