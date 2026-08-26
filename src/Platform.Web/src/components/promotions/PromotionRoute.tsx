import { Link } from 'react-router-dom';
import { ArrowRight } from 'lucide-react';
import { EnvBadge } from '@/components/environments/EnvBadge';
import { shortBranch } from '@/components/builds/BranchBadge';
import { buildRegistryPath } from '@/lib/buildPath';
import { deploymentHistoryPath } from '@/lib/deploymentPath';
import { useSettingsStore } from '@/stores/settingsStore';

/**
 * The synthetic source environment of candidates created from the build registry. Nothing is ever
 * deployed to it, so "from build" says nothing a reader can act on — the branch does. Kept in step
 * with `BuildPromotions.SourceEnv` on the API side.
 */
const BUILD_SOURCE_ENV = 'build';

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
 * pill. Where the build comes from is context, not the headline, so it trails in muted text.
 */
export function PromotionRoute({
  product,
  service,
  sourceEnv,
  targetEnv,
  version,
  targetCurrentVersion,
  sourceBranch,
  size = 'sm',
  className = '',
}: {
  product: string;
  service: string;
  sourceEnv: string;
  targetEnv: string;
  /** The version being promoted — what `targetEnv` ends up on. */
  version: string;
  /** What `targetEnv` runs today, or null for a first deploy there. */
  targetCurrentVersion: string | null;
  /** Full git ref, for candidates promoted straight from the build registry. */
  sourceBranch?: string | null;
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

  // Where the build came from. A real source environment names itself; the build registry's
  // synthetic env doesn't, so a build-sourced candidate shows its branch — and shows nothing at
  // all when the branch is unknown (a registry row removed since, or an older API), because
  // "from build" is the one answer that tells a reader nothing.
  const fromBuild = sourceEnv === BUILD_SOURCE_ENV;
  const branch = sourceBranch?.trim() ? shortBranch(sourceBranch.trim()) : null;
  const origin = fromBuild ? branch : sourceName;
  // …and it links to that origin: the registry row this exact build is (product + service +
  // version is the registry's unique triple), or the source environment's deploy history, which is
  // where a reader goes to see the version that is being promoted actually running.
  const originHref = fromBuild
    ? buildRegistryPath({ product, service, version })
    : deploymentHistoryPath(product, service, sourceEnv);
  const originTitle = fromBuild
    ? `${sourceBranch ?? branch} — open this build in the registry`
    : `Open ${sourceName} deploy history for ${service}`;

  // One tooltip for the whole line — the versions ellipsise on a narrow viewport, and the sentence
  // is what makes the direction unambiguous even when nothing is clipped.
  const provenance = fromBuild
    ? branch
      ? ` Built from ${branch}.`
      : ''
    : ` Build comes from ${sourceName}.`;
  const title = targetCurrentVersion
    ? `Promotes ${version} into ${targetName}, replacing ${targetCurrentVersion}.${provenance}`
    : `Promotes ${version} into ${targetName} — first deploy there.${provenance}`;

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
      {origin && (
        <span className="truncate" style={{ fontSize: dims.source, color: 'var(--text-muted)' }}>
          from{' '}
          <Link
            to={originHref}
            // The line often sits inside a row that navigates to the promotion on click; without
            // this, following the link would also fire the row's own handler.
            onClick={(e) => e.stopPropagation()}
            className="hover:underline"
            style={{ color: 'inherit' }}
            title={originTitle}
          >
            {origin}
          </Link>
        </span>
      )}
    </span>
  );
}
