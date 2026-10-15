import { Snowflake } from 'lucide-react';
import { format, formatDistanceToNow } from 'date-fns';
import type { PromotionFreeze } from '@/lib/api';

/**
 * Banner above the approval card of a Pending candidate held by a Promotion Freeze: names the
 * frozen pair, the reason, who froze it and since when, and that an approval is still recorded.
 */
export function FreezeNotice({ freeze }: { freeze: Omit<PromotionFreeze, 'heldCandidates'> }) {
  const frozenAt = new Date(freeze.frozenAt);
  return (
    <div
      className="rounded-xl border p-4 flex items-start gap-3"
      style={{ borderColor: 'var(--info)', backgroundColor: 'var(--info-bg)', borderLeft: '3px solid var(--info)' }}
    >
      <Snowflake size={16} style={{ color: 'var(--info)', marginTop: 2, flexShrink: 0 }} />
      <div className="min-w-0 space-y-1 text-[13px]">
        <p className="font-semibold" style={{ color: 'var(--text-primary)' }}>
          Promotions to {freeze.targetEnv} are frozen for {freeze.product}
        </p>
        <p style={{ color: 'var(--text-secondary)' }}>{freeze.reason}</p>
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          frozen by {freeze.frozenBy} ·{' '}
          <span title={format(frozenAt, 'MMM d, yyyy HH:mm')}>
            since {formatDistanceToNow(frozenAt, { addSuffix: true })}
          </span>
        </p>
        <p style={{ color: 'var(--text-secondary)' }}>
          Your approval will be recorded, but the promotion will not proceed until the freeze is lifted.
        </p>
      </div>
    </div>
  );
}
