import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Loader2, RotateCcw, Send } from 'lucide-react';
import { marked } from 'marked';
import { api } from '@/lib/api';

marked.setOptions({ gfm: true, breaks: false });

// Dedicated draft / review screen. Reads the window parameters from the URL so the
// page is bookmarkable and refresh-safe. Submitting redirects to the published
// note's permanent detail URL.
export function ReleaseNoteDraftPage() {
  const { product = '' } = useParams<{ product: string }>();
  const [params] = useSearchParams();
  const navigate = useNavigate();

  const environment = params.get('env') ?? '';
  const fromIso = params.get('from') ?? '';
  const toIso = params.get('to') ?? '';

  const [markdown, setMarkdown] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [publishing, setPublishing] = useState(false);

  useEffect(() => {
    let cancelled = false;
    if (!environment || !fromIso || !toIso) {
      setError('Missing environment/from/to in URL.');
      setLoading(false);
      return;
    }
    setLoading(true);
    api
      .getReleaseNotePreview({ product, environment, from: fromIso, to: toIso })
      .then((resp) => { if (!cancelled) { setMarkdown(resp.rendered); setLoading(false); } })
      .catch((e) => { if (!cancelled) { setError(e instanceof Error ? e.message : String(e)); setLoading(false); } });
    return () => { cancelled = true; };
  }, [product, environment, fromIso, toIso]);

  async function publish() {
    setPublishing(true);
    setError(null);
    try {
      const note = await api.generateReleaseNote({
        product,
        environment,
        from: fromIso,
        to: toIso,
        renderedContent: markdown,
      });
      navigate(`/release-notes/${product}/${note.id}`, { replace: true });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setPublishing(false);
    }
  }

  async function resetToTemplate() {
    setLoading(true);
    setError(null);
    try {
      const resp = await api.getReleaseNotePreview({ product, environment, from: fromIso, to: toIso });
      setMarkdown(resp.rendered);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }

  const previewHtml = useMemo(
    () => (markdown ? (marked.parse(markdown) as string) : ''),
    [markdown]
  );

  if (loading) {
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

      <div className="flex items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            New release note — {product}
          </h1>
          <div className="text-[12px] mt-1 font-mono" style={{ color: 'var(--text-muted)' }}>
            {environment} · {fromIso ? new Date(fromIso).toLocaleString() : '?'} → {toIso ? new Date(toIso).toLocaleString() : '?'}
          </div>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={resetToTemplate}
            disabled={loading || publishing}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md border text-[12px] disabled:opacity-50"
            style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
            title="Re-render from the template, discarding manual edits"
          >
            <RotateCcw size={12} /> Reset
          </button>
          <button
            onClick={publish}
            disabled={publishing || !markdown.trim()}
            className="inline-flex items-center gap-2 px-3 py-2 rounded-lg text-[13px] font-medium disabled:opacity-50"
            style={{ backgroundColor: 'var(--accent)', color: 'white' }}
          >
            {publishing ? <Loader2 size={14} className="animate-spin" /> : <Send size={14} />}
            Publish
          </button>
        </div>
      </div>

      {error && (
        <div className="px-3 py-2 rounded-lg text-[13px]" style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}>
          {error}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
        <div className="flex flex-col gap-1">
          <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
            Markdown (editable)
          </div>
          <textarea
            value={markdown}
            onChange={(e) => setMarkdown(e.target.value)}
            rows={32}
            className="font-mono text-[12px] rounded-lg border p-3"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' }}
          />
        </div>
        <div className="flex flex-col gap-1">
          <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
            Rendered preview
          </div>
          <div
            className="release-notes-prose rounded-lg border p-4 text-[13px] overflow-auto"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)', minHeight: '32em' }}
            dangerouslySetInnerHTML={{ __html: previewHtml }}
          />
        </div>
      </div>
    </div>
  );
}
