import { Link } from 'react-router-dom';
import { Database } from 'lucide-react';
import type { MigrationFlag } from '@/lib/types';
import { flagLabel, migrationPath, migrationStatusStyle } from './migrationStatus';

/**
 * The flare on a deployment record: a migration of it is pending, running or failed. Links to the
 * migration, so the click-stop keeps a surrounding row or cell from opening the deployment instead.
 * `compact` drops the words for the matrix, where a cell has room for an icon and a tooltip only.
 * `link={false}` renders a plain chip, for rows that are links themselves.
 */
export function MigrationFlare({ flags, compact = false, link = true }: {
  flags: MigrationFlag[];
  compact?: boolean;
  link?: boolean;
}) {
  if (flags.length === 0) return null;
  // Failed first: one chip per deployment is plenty, and the failure is the one that needs someone.
  const ordered = [...flags].sort((a, b) => rank(a) - rank(b));
  const top = ordered[0];
  const style = migrationStatusStyle(top.status);
  const title = ordered
    .map((f) => `${flagLabel(f)} — v${f.version}${f.failureReason ? `: ${f.failureReason}` : ''}`)
    .join('\n');

  const className =
    'inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full text-[10px] font-semibold whitespace-nowrap transition-opacity hover:opacity-80';
  const chipStyle = {
    backgroundColor: style.bg,
    color: style.fg,
    border: `1px solid color-mix(in srgb, ${style.fg} 30%, transparent)`,
  };
  const body = (
    <>
      <Database size={10} />
      {compact ? top.status : flagLabel(top)}
      {ordered.length > 1 && <span style={{ opacity: 0.7 }}>+{ordered.length - 1}</span>}
    </>
  );

  if (!link) return <span className={className} style={chipStyle} title={title}>{body}</span>;
  return (
    <Link
      to={migrationPath(top.id)}
      onClick={(e) => e.stopPropagation()}
      className={className}
      style={chipStyle}
      title={title}
      aria-label={title}
    >
      {body}
    </Link>
  );
}

function rank(f: MigrationFlag): number {
  return f.status === 'failed' ? 0 : f.status === 'running' ? 1 : 2;
}
