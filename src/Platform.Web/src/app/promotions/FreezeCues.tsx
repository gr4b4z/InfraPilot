import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import { Snowflake, Settings } from 'lucide-react';
import type { PromotionCandidate, PromotionFreeze } from '@/lib/api';
import { freezeCovers, freezeScope } from '@/lib/freezeScope';
import { CollapsibleFreezeBanner } from '@/components/ui/CollapsibleFreezeBanner';

const frozenSince = (iso: string) => formatDistanceToNow(new Date(iso), { addSuffix: true });

/** Active freezes matching the current product / target-env filters, shown above the filters. */
export function FreezeBanner({ freezes, canManage }: { freezes: PromotionFreeze[]; canManage: boolean }) {
  return (
    <CollapsibleFreezeBanner
      title={`${freezes.length === 1 ? '1 active freeze' : `${freezes.length} active freezes`} affecting this list`}
      action={
        canManage && (
          <Link
            to="/settings/freeze"
            className="inline-flex items-center gap-1 text-[12px] font-medium hover:opacity-80"
            style={{ color: 'var(--info)' }}
          >
            <Settings size={12} />
            Manage freezes
          </Link>
        )
      }
    >
      <ul className="space-y-1 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
        {freezes.map((f) => (
          <li key={`${f.product}|${f.service ?? ''}|${f.targetEnv}`} className="flex flex-wrap items-center gap-x-2">
            <span className="font-semibold" style={{ color: 'var(--text-primary)' }}>
              {freezeScope(f)} → {f.targetEnv}
            </span>
            <span>{f.reason}</span>
            <span style={{ color: 'var(--text-muted)' }}>
              · frozen by {f.frozenBy} {frozenSince(f.frozenAt)} · {f.heldCandidates}{' '}
              {f.heldCandidates === 1 ? 'promotion' : 'promotions'} held
            </span>
          </li>
        ))}
      </ul>
    </CollapsibleFreezeBanner>
  );
}

/**
 * Pending candidates held by a freeze, below the open ones. The header names each frozen pair that
 * has a candidate in the group, once. Renders nothing when `candidates` is empty.
 */
export function FrozenGroup({
  freezes,
  candidates,
  children,
}: {
  /** All active freezes; only those matching a candidate in the group are shown. */
  freezes: PromotionFreeze[];
  /** The frozen candidates rendered as `children`. */
  candidates: PromotionCandidate[];
  children: ReactNode;
}) {
  if (candidates.length === 0) return null;
  const pairs = freezes.filter((f) =>
    candidates.some(
      (c) => c.product === f.product && c.targetEnv === f.targetEnv && freezeCovers(f, c.service),
    ),
  );
  return (
    <div>
      <div className="flex items-center justify-between mb-2">
        <h2
          className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider"
          style={{ color: 'var(--info)' }}
        >
          <Snowflake size={11} />
          Frozen ({candidates.length})
        </h2>
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          Approvals are still recorded; nothing is promoted until the freeze is lifted
        </span>
      </div>
      <div
        className="rounded-xl border p-3 space-y-2"
        style={{ borderColor: 'var(--info)', backgroundColor: 'var(--info-bg)' }}
      >
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          {pairs.map((f) => (
            <span key={`${f.product}|${f.service ?? ''}|${f.targetEnv}`} className="flex items-center gap-1.5">
              <span className="font-semibold" style={{ color: 'var(--text-primary)' }}>
                {freezeScope(f)} → {f.targetEnv}
              </span>
              <span>— {f.reason}</span>
              <span style={{ color: 'var(--text-muted)' }}>
                · {f.frozenBy}, {frozenSince(f.frozenAt)}
              </span>
            </span>
          ))}
        </div>
        <div className="space-y-2">{children}</div>
      </div>
    </div>
  );
}
