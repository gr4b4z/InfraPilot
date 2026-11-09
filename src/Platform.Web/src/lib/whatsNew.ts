import { marked } from 'marked';
import { create } from 'zustand';
import { readPref, writePref, WHATS_NEW_SEEN_PREF } from './prefs';

/**
 * The "What's new" changelog: what changed in the portal, told from the side of the people using it.
 *
 * Each change is one Markdown file in `src/app/whats-new/entries/`, named `YYYY-MM-DD-slug.md` and
 * opened by a short frontmatter block — the format and the writing rules are in AGENTS.md ("What's
 * new entries"). One file per change rather than one list, so two PRs adding an entry at the same
 * time never conflict over the top of a shared file.
 *
 * <p>Entries are dated, not versioned. Every merge to main ships as the next patch release, so the PR
 * that adds an entry cannot know which version it will be — but it does know the day it lands.</p>
 */

/**
 * Who a change is for. Approvers are not a role — anyone can be named on an approval gate — so
 * their changes are shown to everybody; QA and admin changes only to those roles by default.
 */
export type Audience = 'everyone' | 'approvers' | 'qa' | 'admins';

export interface ChangelogEntry {
  /** File name without `.md`. Stable, and what the "seen" marker remembers. */
  id: string;
  title: string;
  /** `YYYY-MM-DD`, the day the change was merged. */
  date: string;
  audience: Audience;
  /** The page to try the change on. */
  link: { to: string; label: string } | null;
  /** The body, rendered. Entries are repository content, so this HTML is trusted. */
  html: string;
}

const AUDIENCES: readonly Audience[] = ['everyone', 'approvers', 'qa', 'admins'];
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const FRONTMATTER = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/;

const sources = import.meta.glob<string>('../app/whats-new/entries/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
});

/** A malformed entry is left out with a console warning rather than taking the page down. */
function skip(id: string, reason: string): null {
  console.warn(`What's new: skipped entry "${id}": ${reason}`);
  return null;
}

function parseEntry(path: string, source: string): ChangelogEntry | null {
  const id = path.slice(path.lastIndexOf('/') + 1).replace(/\.md$/, '');
  const match = FRONTMATTER.exec(source);
  if (!match) return skip(id, 'it does not start with a --- frontmatter block');

  const fields: Record<string, string> = {};
  for (const line of match[1].split(/\r?\n/)) {
    const colon = line.indexOf(':');
    if (colon <= 0) continue;
    fields[line.slice(0, colon).trim()] = line
      .slice(colon + 1)
      .trim()
      .replace(/^(["'])(.*)\1$/, '$2');
  }

  const { title, date, link, linkLabel } = fields;
  if (!title) return skip(id, 'it has no title');
  if (!date || !DATE.test(date)) return skip(id, `date must be YYYY-MM-DD, got "${date ?? ''}"`);

  let audience: Audience = 'everyone';
  if (fields.audience) {
    if ((AUDIENCES as readonly string[]).includes(fields.audience)) audience = fields.audience as Audience;
    else console.warn(`What's new: entry "${id}" has unknown audience "${fields.audience}", showing it to everyone`);
  }

  // Portal paths only: an entry describes the portal, and a relative path stays inside the app.
  const to = link?.startsWith('/') ? link : null;
  if (link && !to) console.warn(`What's new: entry "${id}" link must be a portal path starting with /`);

  return {
    id,
    title,
    date,
    audience,
    link: to ? { to, label: linkLabel || 'Open' } : null,
    html: marked.parse(source.slice(match[0].length).trim(), { gfm: true, breaks: false, async: false }),
  };
}

/**
 * Every entry, newest day first. Within a day, the changes that reach the most people lead — in
 * {@link AUDIENCES} order — then file-name order.
 */
export const CHANGELOG: ChangelogEntry[] = Object.entries(sources)
  .map(([path, source]) => parseEntry(path, source))
  .filter((entry): entry is ChangelogEntry => entry !== null)
  .sort(
    (a, b) =>
      b.date.localeCompare(a.date) ||
      AUDIENCES.indexOf(a.audience) - AUDIENCES.indexOf(b.audience) ||
      a.id.localeCompare(b.id),
  );

/** The audiences a person sees without asking for the rest. Admins see everything. */
export function audiencesFor(user: { isQA?: boolean; isAdmin?: boolean } | null): ReadonlySet<Audience> {
  if (user?.isAdmin) return new Set(AUDIENCES);
  if (user?.isQA) return new Set<Audience>(['everyone', 'approvers', 'qa']);
  return new Set<Audience>(['everyone', 'approvers']);
}

/**
 * What this browser has already been shown: the newest entry date at the last visit, plus the ids of
 * that day's entries — so a change merged later on the same day still counts as new.
 */
export interface SeenMarker {
  date: string;
  ids: string[];
}

function readSeen(): SeenMarker | null {
  const raw = readPref(WHATS_NEW_SEEN_PREF);
  if (!raw) return null;
  const [date, ids = ''] = raw.split('|');
  if (!DATE.test(date)) return null;
  return { date, ids: ids ? ids.split(',') : [] };
}

/**
 * With no marker — a browser that has never opened the page — only the last month counts as new.
 * Nudging someone about every change since the changelog began is noise, not news.
 */
const FIRST_VISIT_WINDOW_DAYS = 30;

function isoDaysAgo(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() - days);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function isUnseen(entry: ChangelogEntry, seen: SeenMarker | null): boolean {
  if (!seen) return entry.date >= isoDaysAgo(FIRST_VISIT_WINDOW_DAYS);
  return entry.date > seen.date || (entry.date === seen.date && !seen.ids.includes(entry.id));
}

interface WhatsNewState {
  seen: SeenMarker | null;
  /** Opening the page counts as seeing everything on it, whichever roles it was filtered to. */
  markAllSeen: () => void;
}

export const useWhatsNewStore = create<WhatsNewState>()((set) => ({
  seen: readSeen(),
  markAllSeen: () => {
    const newest = CHANGELOG[0];
    if (!newest) return;
    const seen: SeenMarker = {
      date: newest.date,
      ids: CHANGELOG.filter((entry) => entry.date === newest.date).map((entry) => entry.id),
    };
    writePref(WHATS_NEW_SEEN_PREF, `${seen.date}|${seen.ids.join(',')}`);
    set({ seen });
  },
}));

/** How many changes meant for this person they have not seen yet. Drives the topbar link. */
export function useUnseenChangeCount(user: { isQA?: boolean; isAdmin?: boolean } | null): number {
  const seen = useWhatsNewStore((s) => s.seen);
  const audiences = audiencesFor(user);
  return CHANGELOG.filter((entry) => audiences.has(entry.audience) && isUnseen(entry, seen)).length;
}
