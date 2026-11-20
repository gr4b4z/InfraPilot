import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { format, parseISO } from 'date-fns';
import { ArrowRight, Megaphone } from 'lucide-react';
import { useAuthStore } from '@/stores/authStore';
import { useDocumentTitle } from '@/lib/pageTitle';
import { getAppName } from '@/lib/runtimeConfig';
import {
  CHANGELOG,
  audiencesFor,
  isUnseen,
  useWhatsNewStore,
  type Audience,
  type ChangelogEntry,
  type SeenMarker,
} from '@/lib/whatsNew';

/** Changes for everyone carry no chip — a chip on every card would say nothing. */
const AUDIENCE_LABEL: Record<Audience, string | null> = {
  everyone: null,
  approvers: 'For approvers',
  qa: 'For QA',
  admins: 'For admins',
};

/**
 * "What's new" — the portal's changelog, written for the people using it rather than for the people
 * building it. Reached from the topbar link that appears when there is something unseen, the user
 * menu, and the build version at the foot of the sidebar.
 */
export function WhatsNewPage() {
  useDocumentTitle(["What's new"]);
  const user = useAuthStore((s) => s.user);
  const isAdmin = user?.isAdmin ?? false;
  const markAllSeen = useWhatsNewStore((s) => s.markAllSeen);
  // What was unseen when the page opened, captured before the visit marks it all seen — otherwise
  // the "New" badges would vanish on the very render that is meant to show them.
  const [seenBefore] = useState(() => useWhatsNewStore.getState().seen);
  const [showAllRoles, setShowAllRoles] = useState(false);

  useEffect(() => {
    markAllSeen();
  }, [markAllSeen]);

  const audiences = audiencesFor(user);
  const forMe = CHANGELOG.filter((entry) => audiences.has(entry.audience));
  const otherRoles = CHANGELOG.length - forMe.length;
  const groups = groupByDate(showAllRoles ? CHANGELOG : forMe);

  return (
    <div className="max-w-3xl space-y-8">
      <div>
        <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
          What's new
        </h1>
        <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
          New features and changes in {getAppName()}, newest first. Bug fixes aren't listed.
          {__APP_VERSION__ !== 'dev' && <> You're on version {__APP_VERSION__}.</>}
        </p>
        {/* Admins already see every role's changes. */}
        {!isAdmin && otherRoles > 0 && (
          <label
            className="inline-flex items-center gap-2 mt-3 text-[13px] cursor-pointer select-none"
            style={{ color: 'var(--text-secondary)' }}
          >
            <input
              type="checkbox"
              checked={showAllRoles}
              onChange={(e) => setShowAllRoles(e.target.checked)}
            />
            Also show changes for other roles ({otherRoles})
          </label>
        )}
      </div>

      {groups.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-20 text-center">
          <Megaphone size={40} style={{ color: 'var(--text-muted)' }} />
          <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>Nothing new yet</p>
        </div>
      ) : (
        groups.map((group) => (
          <section key={group.date} aria-label={formatDay(group.date)}>
            <h2
              className="text-[11px] font-semibold uppercase tracking-wider mb-3"
              style={{ color: 'var(--text-muted)' }}
            >
              {formatDay(group.date)}
            </h2>
            <div className="space-y-3">
              {group.entries.map((entry) => (
                <EntryCard
                  key={entry.id}
                  entry={entry}
                  seenBefore={seenBefore}
                  // Settings pages redirect anyone but an admin, so their links would go nowhere.
                  canFollowLink={entry.audience !== 'admins' || isAdmin}
                />
              ))}
            </div>
          </section>
        ))
      )}
    </div>
  );
}

function EntryCard({
  entry,
  seenBefore,
  canFollowLink,
}: {
  entry: ChangelogEntry;
  seenBefore: SeenMarker | null;
  canFollowLink: boolean;
}) {
  const audienceLabel = AUDIENCE_LABEL[entry.audience];
  return (
    <article
      className="rounded-xl border p-4"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          {entry.title}
        </h3>
        {isUnseen(entry, seenBefore) && (
          <span className="badge" style={{ backgroundColor: 'var(--info-bg)', color: 'var(--info)' }}>
            New
          </span>
        )}
        {audienceLabel && (
          <span
            className="badge"
            style={{ backgroundColor: 'var(--bg-secondary)', color: 'var(--text-muted)' }}
          >
            {audienceLabel}
          </span>
        )}
      </div>
      <div
        className="release-notes-prose text-[13px] leading-relaxed mt-1.5"
        style={{ color: 'var(--text-secondary)' }}
        dangerouslySetInnerHTML={{ __html: entry.html }}
      />
      {entry.link && canFollowLink && (
        <Link
          to={entry.link.to}
          className="inline-flex items-center gap-1 mt-2 text-[12px] font-medium hover:underline"
          style={{ color: 'var(--info)' }}
        >
          {entry.link.label}
          <ArrowRight size={12} />
        </Link>
      )}
    </article>
  );
}

function groupByDate(entries: ChangelogEntry[]): { date: string; entries: ChangelogEntry[] }[] {
  const groups: { date: string; entries: ChangelogEntry[] }[] = [];
  for (const entry of entries) {
    const last = groups[groups.length - 1];
    if (last?.date === entry.date) last.entries.push(entry);
    else groups.push({ date: entry.date, entries: [entry] });
  }
  return groups;
}

function formatDay(date: string): string {
  return format(parseISO(date), 'd MMMM yyyy');
}
