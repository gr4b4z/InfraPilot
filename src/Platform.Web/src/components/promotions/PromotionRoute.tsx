import { ArrowRight } from 'lucide-react';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { useSettingsStore } from '@/stores/settingsStore';

/**
 * What a promotion actually does, in one line: the environment it lands in, and how that
 * environment's version moves.
 *
 *     [Staging]  5.1.87-g0e2efdcb → 5.1.90-g3f62b443   from Staging Candidate
 *
 * This replaced a symmetric `source (newVersion) → target (currentVersion)` pair of pills, which
 * read as a *downgrade* to anybody scanning quickly: the higher version sat on the left and the
 * lower one on the right, because the two numbers belonged to different environments rather than
 * to one progression. People already read the version flow the other way round in their deploy
 * notifications ("service: (5.23.108 → 5.23.113)"), so the arrow here means the same thing it does
 * there — old version on the left, new version on the right, both for the environment named by the
 * pill. The source environment is where the build comes from; it's context, not the headline, so it
 * trails in muted text.
 */
export function PromotionRoute({
  sourceEnv,
  targetEnv,
  version,
  targetCurrentVersion,
  size = 'sm',
  className = '',
}: {
  sourceEnv: string;
  targetEnv: string;
  /** The version being promoted — what `targetEnv` ends up on. */
  version: string;
  /** What `targetEnv` runs today, or null for a first deploy there. */
  targetCurrentVersion: string | null;
  /** `xs` for dense rows, `sm` (default) for cards and page headers. */
  size?: 'xs' | 'sm';
  className?: string;
}) {
  const sourceName = useSettingsStore((s) => s.getDisplayName(sourceEnv));
  const targetName = useSettingsStore((s) => s.getDisplayName(targetEnv));

  const dims =
    size === 'xs'
      ? { version: 11, source: 10, arrow: 11 }
      : { version: 12, source: 11, arrow: 12 };

  // One tooltip for the whole line — the versions ellipsise on a narrow viewport, and the sentence
  // is what makes the direction unambiguous even when nothing is clipped.
  const title = targetCurrentVersion
    ? `Promotes ${version} into ${targetName}, replacing ${targetCurrentVersion}. Build comes from ${sourceName}.`
    : `Promotes ${version} into ${targetName} — first deploy there. Build comes from ${sourceName}.`;

  return (
    <span
      className={`inline-flex flex-wrap items-center gap-x-2 gap-y-1 min-w-0 ${className}`}
      title={title}
    >
      <EnvBadge env={targetEnv} size={size} title={title} />
      <span
        className="inline-flex items-center gap-1.5 min-w-0 font-mono"
        style={{ fontSize: dims.version }}
      >
        {targetCurrentVersion ? (
          <span
            className="truncate"
            style={{ color: 'var(--text-muted)' }}
          >
            {targetCurrentVersion}
          </span>
        ) : (
          <span className="shrink-0 font-sans" style={{ color: 'var(--text-muted)' }}>
            first deploy
          </span>
        )}
        <ArrowRight size={dims.arrow} className="shrink-0" style={{ color: 'var(--text-muted)' }} />
        <span className="truncate font-medium" style={{ color: 'var(--text-primary)' }}>
          {version}
        </span>
      </span>
      <span
        className="whitespace-nowrap"
        style={{ fontSize: dims.source, color: 'var(--text-muted)' }}
      >
        from {sourceName}
      </span>
    </span>
  );
}
