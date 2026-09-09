import {
  BookOpen,
  Bug,
  ChevronDown,
  ChevronsDown,
  ChevronsUp,
  ChevronUp,
  Equal,
  Flag,
  Layers,
  ListTree,
  Sparkles,
  SquareCheck,
  Ticket,
} from 'lucide-react';

/**
 * The two tracker labels a work item carries besides its name: what kind of thing it is (Jira's
 * issue type — Bug, Story, Task…) and how urgent (its priority — Highest … Lowest). Both arrive as
 * the tracker's own words on the ingest payload and are shown as they came; this module only
 * recognises the common Jira names well enough to pick an icon and a colour, and falls back to a
 * neutral chip with the raw label for anything else. A tracker that calls its levels "P1" or
 * "Sev-2" still gets a legible chip, just not a coloured one.
 *
 * One component for every surface — detail header, promotion bundle rows, the sign-off queues, the
 * deployment's work-item list — so a Bug looks like a Bug everywhere.
 */

type TypeKind = 'bug' | 'story' | 'epic' | 'subtask' | 'improvement' | 'task' | 'other';

/** Buckets a tracker issue type by the common Jira names; anything unrecognised is 'other'. */
function typeKind(type: string | null | undefined): TypeKind {
  const t = (type ?? '').trim().toLowerCase();
  if (t.includes('bug') || t.includes('defect') || t.includes('incident')) return 'bug';
  if (t.includes('story')) return 'story';
  if (t.includes('epic') || t.includes('initiative') || t.includes('feature')) return 'epic';
  if (t.includes('sub')) return 'subtask';
  if (t.includes('improvement') || t.includes('enhancement') || t.includes('spike')) return 'improvement';
  if (t.includes('task') || t.includes('chore')) return 'task';
  return 'other';
}

function WorkItemTypeIcon({ type, size }: { type: string | null | undefined; size: number }) {
  switch (typeKind(type)) {
    case 'bug':
      return <Bug size={size} aria-hidden="true" />;
    case 'story':
      return <BookOpen size={size} aria-hidden="true" />;
    case 'epic':
      return <Layers size={size} aria-hidden="true" />;
    case 'subtask':
      return <ListTree size={size} aria-hidden="true" />;
    case 'improvement':
      return <Sparkles size={size} aria-hidden="true" />;
    case 'task':
      return <SquareCheck size={size} aria-hidden="true" />;
    default:
      return <Ticket size={size} aria-hidden="true" />;
  }
}

/**
 * Colour for a tracker issue type. Only a Bug is singled out: it's the one type where the kind of
 * ticket changes what a reviewer looks for. Everything else is neutral so the chips read as labels
 * rather than a second status column.
 */
function workItemTypeColor(type: string | null | undefined): string {
  return typeKind(type) === 'bug' ? 'var(--danger)' : 'var(--text-secondary)';
}

/** 0 = most urgent … 4 = least urgent; 2 for anything unrecognised. */
type PriorityRank = 0 | 1 | 2 | 3 | 4;

/**
 * Ranks a tracker priority. Jira's five stock levels and the older Blocker / Critical / Major /
 * Minor / Trivial scheme (plus P0–P5) collapse onto five ranks; an unrecognised label lands in the
 * middle so it sorts with "Medium" rather than at either extreme.
 */
function priorityRank(priority: string | null | undefined): PriorityRank | null {
  const p = (priority ?? '').trim().toLowerCase();
  if (!p) return null;
  if (p === 'highest' || p === 'blocker' || p === 'critical' || p === 'urgent' || p === 'p0' || p === 'p1') return 0;
  if (p === 'high' || p === 'major' || p === 'p2') return 1;
  if (p === 'medium' || p === 'normal' || p === 'moderate' || p === 'p3') return 2;
  if (p === 'low' || p === 'minor' || p === 'p4') return 3;
  if (p === 'lowest' || p === 'trivial' || p === 'p5') return 4;
  return 2;
}

/** Whether the label was one of the names {@link priorityRank} recognises (vs. defaulted). */
function isKnownPriority(priority: string | null | undefined): boolean {
  const p = (priority ?? '').trim().toLowerCase();
  return /^(highest|blocker|critical|urgent|p[0-5]|high|major|medium|normal|moderate|low|minor|lowest|trivial)$/.test(p);
}

/**
 * Tone for a priority rank: the top two levels alarm, the middle is neutral, the bottom two are
 * muted. Three tones rather than five because the chip also carries the label — colour only needs
 * to say "look at this first" or "this can wait".
 */
function priorityTone(rank: PriorityRank | null): { color: string; bg: string } {
  switch (rank) {
    case 0:
      return { color: 'var(--danger)', bg: 'var(--danger-bg)' };
    case 1:
      return { color: 'var(--warning)', bg: 'var(--warning-bg)' };
    case 3:
    case 4:
      return { color: 'var(--text-muted)', bg: 'var(--bg-secondary)' };
    default:
      return { color: 'var(--text-secondary)', bg: 'var(--bg-secondary)' };
  }
}

function PriorityIcon({ priority, size }: { priority: string | null | undefined; size: number }) {
  // A label the ranking didn't recognise gets a flag rather than the "medium" glyph: the glyph
  // would claim a level nobody stated.
  if (!isKnownPriority(priority)) return <Flag size={size} aria-hidden="true" />;
  switch (priorityRank(priority)) {
    case 0:
      return <ChevronsUp size={size} aria-hidden="true" />;
    case 1:
      return <ChevronUp size={size} aria-hidden="true" />;
    case 3:
      return <ChevronDown size={size} aria-hidden="true" />;
    case 4:
      return <ChevronsDown size={size} aria-hidden="true" />;
    default:
      return <Equal size={size} aria-hidden="true" />;
  }
}

/** Chip naming the tracker issue type ("Bug", "Story"). Renders nothing when there is none. */
export function WorkItemTypeBadge({ type }: { type: string | null | undefined }) {
  const label = (type ?? '').trim();
  if (!label) return null;
  return (
    <span
      className="badge shrink-0"
      style={{ backgroundColor: 'var(--bg-secondary)', color: workItemTypeColor(label) }}
      title={`Type: ${label}`}
    >
      <WorkItemTypeIcon type={label} size={10} />
      {label}
    </span>
  );
}

/** Chip naming the tracker priority ("High"). Renders nothing when there is none. */
export function WorkItemPriorityBadge({ priority }: { priority: string | null | undefined }) {
  const label = (priority ?? '').trim();
  if (!label) return null;
  const { color, bg } = priorityTone(priorityRank(label));
  return (
    <span className="badge shrink-0" style={{ backgroundColor: bg, color }} title={`Priority: ${label}`}>
      <PriorityIcon priority={label} size={10} />
      {label}
    </span>
  );
}

/**
 * Type and priority side by side, type first — "what is it" before "how urgent". Renders nothing at
 * all when the producer sent neither, so a row from a producer that never asked the tracker keeps
 * its layout instead of gaining an empty gap or an "Unknown" chip.
 */
export function WorkItemTraits({
  workItemType,
  priority,
  className,
}: {
  workItemType: string | null | undefined;
  priority: string | null | undefined;
  className?: string;
}) {
  const hasType = !!(workItemType ?? '').trim();
  const hasPriority = !!(priority ?? '').trim();
  if (!hasType && !hasPriority) return null;
  return (
    <span className={`inline-flex items-center gap-1 shrink-0 ${className ?? ''}`}>
      <WorkItemTypeBadge type={workItemType} />
      <WorkItemPriorityBadge priority={priority} />
    </span>
  );
}
