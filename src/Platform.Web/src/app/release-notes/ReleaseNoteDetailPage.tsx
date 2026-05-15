import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, Check, Copy, Loader2 } from 'lucide-react';
import { marked } from 'marked';
import { api, type ReleaseNoteDetail } from '@/lib/api';

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
          <div className="inline-flex items-center rounded-lg overflow-hidden border" style={{ borderColor: 'var(--border-color)' }}>
            {(['rendered', 'services'] as const).map((v) => (
              <button
                key={v}
                onClick={() => setView(v)}
                className="px-3 py-1.5 text-[12px] capitalize"
                style={{
                  backgroundColor: view === v ? 'var(--accent-subtle)' : 'transparent',
                  color: view === v ? 'var(--accent)' : 'var(--text-secondary)',
                }}
              >{v}</button>
            ))}
          </div>
        </div>
      </div>

      {view === 'rendered' ? (
        <div
          className="release-notes-prose rounded-xl border p-6 text-[14px] overflow-x-auto"
          style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
          dangerouslySetInnerHTML={{ __html: html }}
        />
      ) : (
        <div className="space-y-3">
          {note.raw.services.map((svc) => (
            <div
              key={svc.service}
              className="rounded-xl border p-4"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
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
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
