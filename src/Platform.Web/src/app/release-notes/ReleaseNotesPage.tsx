import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Loader2, ScrollText, Eye, ChevronLeft, ChevronRight, ExternalLink } from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';
import { marked } from 'marked';
import { api, type ReleaseNoteFeedItem } from '@/lib/api';
import { useDeploymentStore } from '@/stores/deploymentStore';

// Content originates from our own server-side template engine, so we render the
// stored markdown synchronously without further sanitisation (same as the detail page).
marked.setOptions({ gfm: true, breaks: false });

const PAGE_SIZE = 10;

// `<input type="datetime-local">` consumes/emits "yyyy-MM-ddTHH:mm" in local time.
function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function ReleaseNotesPage() {
  const { product = '' } = useParams<{ product: string }>();
  const navigate = useNavigate();
  const { products, fetchProducts } = useDeploymentStore();
  const [items, setItems] = useState<ReleaseNoteFeedItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [environment, setEnvironment] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState<string | null>(null);
  const [to, setTo] = useState<string | null>(null);
  // Newest note's timestamp, captured from page 1 so the "from" default stays
  // correct even while browsing older pages.
  const [newestAt, setNewestAt] = useState<string | null>(null);

  useEffect(() => { fetchProducts(); }, [fetchProducts]);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const res = await api.listReleaseNotes({
        product,
        environment: environment || undefined,
        page,
        pageSize: PAGE_SIZE,
      });
      setItems(res.items);
      setTotal(res.total);
      if (res.page === 1) setNewestAt(res.items[0]?.generatedAt ?? null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }

  // Reset to the first page whenever the filter changes.
  useEffect(() => {
    setPage(1);
  }, [product, environment]);

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [product, environment, page]);

  const productEntry = products.find((p) => p.product === product);
  const envsFromProduct = productEntry ? Object.keys(productEntry.environments) : [];
  const environments = Array.from(
    new Set([...envsFromProduct, ...items.map((i) => i.environment)])
  ).sort();

  const defaultWindow = useMemo(() => {
    const now = new Date();
    const start = newestAt ? new Date(newestAt) : new Date(now.getTime() - 24 * 60 * 60 * 1000);
    return { from: toLocalInput(start), to: toLocalInput(now) };
  }, [newestAt]);

  const fromValue = from ?? defaultWindow.from;
  const toValue = to ?? defaultWindow.to;

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  function openDraft() {
    if (!environment) { setError('Pick an environment before previewing'); return; }
    const qs = new URLSearchParams({
      env: environment,
      from: new Date(fromValue).toISOString(),
      to: new Date(toValue).toISOString(),
    });
    navigate(`/release-notes/${product}/new?${qs.toString()}`);
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          Release Notes — {product}
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          Generated release notes aggregated from deployment events
        </p>
      </div>

      <div className="rounded-xl border p-4 space-y-3" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
        <div className="text-[11px] uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
          New release note
        </div>
        <div className="flex flex-wrap items-end gap-3">
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>Environment</label>
            <select
              value={environment}
              onChange={(e) => setEnvironment(e.target.value)}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            >
              <option value="">— select —</option>
              {environments.map((env) => <option key={env} value={env}>{env}</option>)}
            </select>
          </div>
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>From (since last by default)</label>
            <input
              type="datetime-local"
              value={fromValue}
              onChange={(e) => { setFrom(e.target.value); if (to === null) setTo(defaultWindow.to); }}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            />
          </div>
          <div className="flex flex-col gap-1">
            <label className="text-[11px]" style={{ color: 'var(--text-muted)' }}>To (now by default)</label>
            <input
              type="datetime-local"
              value={toValue}
              onChange={(e) => { setTo(e.target.value); if (from === null) setFrom(defaultWindow.from); }}
              className="px-2 py-1.5 rounded-md border text-[13px]"
              style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
            />
          </div>
          {(from !== null || to !== null) && (
            <button
              onClick={() => { setFrom(null); setTo(null); }}
              className="text-[12px] underline"
              style={{ color: 'var(--text-muted)' }}
              title="Reset to auto window (since last note → now)"
            >Reset</button>
          )}
          <div className="ml-auto">
            <button
              onClick={openDraft}
              disabled={!environment}
              className="inline-flex items-center gap-2 px-3 py-2 rounded-lg text-[13px] font-medium disabled:opacity-50"
              style={{ backgroundColor: 'var(--accent)', color: 'white' }}
            >
              <Eye size={14} />
              Preview
            </button>
          </div>
        </div>
      </div>

      {error && (
        <div className="px-3 py-2 rounded-lg text-[13px]" style={{ backgroundColor: 'var(--error-bg)', color: 'var(--error)' }}>
          {error}
        </div>
      )}

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : items.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <ScrollText size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>No release notes yet</p>
        </div>
      ) : (
        <div className="space-y-6">
          {items.map((note) => (
            <ReleaseNoteCard
              key={note.id}
              note={note}
              onOpen={() => navigate(`/release-notes/${product}/${note.id}`)}
            />
          ))}

          <div className="flex items-center justify-between pt-2">
            <div className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
              {total} note{total === 1 ? '' : 's'} · page {page} of {totalPages}
            </div>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                disabled={page <= 1}
                className="inline-flex items-center gap-1 px-2.5 py-1.5 rounded-md border text-[12px] disabled:opacity-40"
                style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
              >
                <ChevronLeft size={14} /> Prev
              </button>
              <button
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                className="inline-flex items-center gap-1 px-2.5 py-1.5 rounded-md border text-[12px] disabled:opacity-40"
                style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
              >
                Next <ChevronRight size={14} />
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function ReleaseNoteCard({ note, onOpen }: { note: ReleaseNoteFeedItem; onOpen: () => void }) {
  const html = useMemo(() => marked.parse(note.renderedContent) as string, [note.renderedContent]);
  return (
    <div
      className="rounded-xl border overflow-hidden"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div
        className="flex items-center justify-between gap-3 px-5 py-3"
        style={{ borderBottom: '1px solid var(--border-color)' }}
      >
        <div className="flex items-baseline gap-3 flex-wrap">
          <span className="font-semibold text-[14px]" style={{ color: 'var(--text-primary)' }}>{note.environment}</span>
          <span className="text-[12px] font-mono" style={{ color: 'var(--text-muted)' }}>
            {new Date(note.from).toLocaleDateString()} → {new Date(note.to).toLocaleDateString()}
          </span>
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {note.servicesCount} service{note.servicesCount === 1 ? '' : 's'}
          </span>
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {formatDistanceToNow(new Date(note.generatedAt), { addSuffix: true })}
          </span>
        </div>
        <button
          onClick={onOpen}
          className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-md border text-[12px] shrink-0"
          style={{ borderColor: 'var(--border-color)', color: 'var(--text-secondary)' }}
          title="Open full note"
        >
          <ExternalLink size={12} /> Open
        </button>
      </div>
      <div
        className="release-notes-prose px-5 py-4 text-[14px] overflow-x-auto"
        style={{ color: 'var(--text-primary)' }}
        dangerouslySetInnerHTML={{ __html: html }}
      />
    </div>
  );
}
