import { useState } from 'react';
import { ExternalLink, FileText, GitBranch, GitPullRequest, Plus, Tag, Ticket, Workflow, X } from 'lucide-react';
import type { ReleaseLink, ReleaseRun } from '@/lib/api';
import { inputClass, inputStyle } from '@/app/settings/formStyles';
import { describeLink, type LinkKind } from './releaseStatus';

const KIND_ICON: Record<LinkKind, typeof ExternalLink> = {
  run: Workflow,
  ticket: Ticket,
  doc: FileText,
  pr: GitPullRequest,
  tag: Tag,
  repo: GitBranch,
  link: ExternalLink,
};

const fmt = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : null;

/** One outside link as a chip: an icon for what it is, and its name. Opens in a new tab. */
export function LinkChip({ link }: { link: ReleaseLink }) {
  const { text, kind } = describeLink(link.url, link.label);
  const Icon = KIND_ICON[kind];
  return (
    <a
      href={link.url}
      target="_blank"
      rel="noopener noreferrer"
      title={link.url}
      className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md text-[12px] border transition-colors hover:border-[var(--accent)]"
      style={{ borderColor: 'var(--border-color)', color: 'var(--text-primary)', backgroundColor: 'var(--bg-primary)' }}
    >
      <Icon size={12} style={{ color: 'var(--accent)' }} />
      <span className="truncate max-w-[22rem]">{text}</span>
    </a>
  );
}

export function LinkList({ links }: { links: ReleaseLink[] }) {
  if (links.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1.5">
      {links.map((l) => <LinkChip key={l.url} link={l} />)}
    </div>
  );
}

/**
 * Edit a list of links: an address and an optional name per row. Rows without an address are
 * dropped on save, so an empty row left behind is harmless.
 */
export function LinksEditor({ links, onChange }: { links: ReleaseLink[]; onChange: (next: ReleaseLink[]) => void }) {
  const update = (i: number, patch: Partial<ReleaseLink>) =>
    onChange(links.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  return (
    <div className="space-y-1.5">
      {links.map((l, i) => (
        <div key={i} className="flex gap-1.5">
          <input
            value={l.url}
            onChange={(e) => update(i, { url: e.target.value })}
            placeholder="https://… pipeline run, ticket, page"
            className={`${inputClass} flex-[2] min-w-0`}
            style={inputStyle}
            aria-label={`Link ${i + 1} address`}
          />
          <input
            value={l.label ?? ''}
            onChange={(e) => update(i, { label: e.target.value })}
            placeholder={l.url ? describeLink(l.url).text : 'Name (optional)'}
            className={`${inputClass} flex-1 min-w-0`}
            style={inputStyle}
            aria-label={`Link ${i + 1} name`}
          />
          <button
            type="button"
            onClick={() => onChange(links.filter((_, j) => j !== i))}
            className="px-1.5 rounded hover:bg-[var(--bg-secondary)]"
            aria-label={`Remove link ${i + 1}`}
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={14} />
          </button>
        </div>
      ))}
      <button
        type="button"
        onClick={() => onChange([...links, { label: null, url: '' }])}
        className="inline-flex items-center gap-1 text-[12px] hover:underline"
        style={{ color: 'var(--accent)' }}
      >
        <Plus size={12} /> Add link
      </button>
    </div>
  );
}

/**
 * The pipeline runs behind a check — SDP runs, mpt-release workflow runs — each with how many of the
 * release's services it handled and how many attempts failed. Read off the deploys and migrations
 * themselves, so nobody has to paste them in. Folded, it shows the runs that carried the most services
 * (the release-day batch, not the last one-service hotfix); either way they are listed oldest first.
 */
export function RunList({ runs, noun }: { runs: ReleaseRun[]; noun: string }) {
  const [showAll, setShowAll] = useState(false);
  if (runs.length === 0) return null;
  const biggest = new Set(
    [...runs]
      .sort((a, b) => b.services - a.services || b.failed - a.failed || (b.lastAt ?? '').localeCompare(a.lastAt ?? ''))
      .slice(0, 4)
      .map((r) => r.url),
  );
  const shown = showAll ? runs : runs.filter((r) => biggest.has(r.url));
  return (
    <div className="mt-2">
      <div className="text-[11px] font-medium uppercase tracking-wider" style={{ color: 'var(--text-muted)' }}>
        Pipeline runs ({runs.length})
      </div>
      {runs.length > 4 && !showAll && (
        <button className="text-[11px] hover:underline" style={{ color: 'var(--accent)' }} onClick={() => setShowAll(true)}>
          Show all {runs.length}
        </button>
      )}
      <ul className="mt-1 space-y-0.5 text-[12px]">
        {shown.map((r) => {
          const { kind } = describeLink(r.url);
          const Icon = KIND_ICON[kind === 'link' ? 'run' : kind];
          const range = r.firstAt && r.lastAt && fmt(r.firstAt) !== fmt(r.lastAt) ? `${fmt(r.firstAt)} – ${fmt(r.lastAt)}` : fmt(r.lastAt ?? r.firstAt);
          return (
            <li key={r.url} className="flex items-center gap-1.5 flex-wrap">
              <Icon size={12} style={{ color: 'var(--accent)' }} />
              <a href={r.url} target="_blank" rel="noopener noreferrer" className="font-medium hover:underline" style={{ color: 'var(--text-primary)' }} title={r.url}>
                {r.label}
              </a>
              <span style={{ color: 'var(--text-muted)' }}>
                {r.services} {r.services === 1 ? noun : `${noun}s`}
                {r.failed > 0 && <span style={{ color: 'var(--danger)' }}> · {r.failed} failed</span>}
                {range && ` · ${range}`}
              </span>
            </li>
          );
        })}
      </ul>
      {showAll && runs.length > 4 && (
        <button className="text-[11px] hover:underline mt-0.5" style={{ color: 'var(--accent)' }} onClick={() => setShowAll(false)}>
          Show the 4 biggest
        </button>
      )}
    </div>
  );
}
