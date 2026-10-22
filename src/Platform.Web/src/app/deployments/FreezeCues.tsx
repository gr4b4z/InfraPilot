import { Link } from 'react-router-dom';
import { format, formatDistanceToNow } from 'date-fns';
import { Settings, Snowflake } from 'lucide-react';
import type { PromotionFreezeWindow } from '@/lib/api';
import { freezeScope } from '@/lib/freezeScope';

/*
 * Promotion Freeze cues for the deployments pages (GLOSSARY.md, ADR-0001/0002/0003). A freeze only holds
 * promotions, so these pages say two things: which environments of the product are frozen right now
 * (banner, column / card snowflake, held promotion chips), and which deploys landed anyway while a
 * freeze was on — a promotion approved before it, an Administrator Bypass, or a pipeline that does
 * not go through promotions at all. The periods themselves come from freezeWindows.ts.
 */

const since = (iso: string) => formatDistanceToNow(new Date(iso), { addSuffix: true });

/**
 * Above a product's (or service's) deployments: every environment frozen for it right now, with the
 * reason, who froze it, since when and how many promotions it holds here.
 */
export function DeploymentFreezeBanner({
  freezes,
  heldIn,
  canManage,
  getDisplayName,
}: {
  /** Active freezes of this product. Renders nothing when empty. */
  freezes: PromotionFreezeWindow[];
  /** Pending promotions into `env` held by its freeze, on this page; undefined hides the count. */
  heldIn?: (env: string) => number;
  canManage: boolean;
  getDisplayName: (env: string) => string;
}) {
  if (freezes.length === 0) return null;
  const title =
    freezes.length === 1
      ? freezes[0].service
        ? `Promotions of ${freezes[0].service} to ${getDisplayName(freezes[0].targetEnv)} are frozen`
        : `Promotions to ${getDisplayName(freezes[0].targetEnv)} are frozen`
      : `${freezes.length} promotion freezes are active`;
  return (
    <div
      data-guide-anchor="deployments-freeze-banner"
      className="rounded-xl border p-3 flex items-start gap-3"
      style={{ borderColor: 'var(--info)', backgroundColor: 'var(--info-bg)', borderLeft: '3px solid var(--info)' }}
    >
      <Snowflake size={16} style={{ color: 'var(--info)', marginTop: 2, flexShrink: 0 }} />
      <div className="flex-1 min-w-0 space-y-1.5">
        <div className="flex items-center justify-between gap-3">
          <p className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
            {title}
          </p>
          {canManage && (
            <Link
              to="/settings/freeze"
              className="inline-flex items-center gap-1 text-[12px] font-medium hover:opacity-80"
              style={{ color: 'var(--info)' }}
            >
              <Settings size={12} />
              Manage freezes
            </Link>
          )}
        </div>
        <ul className="space-y-1 text-[12px]" style={{ color: 'var(--text-secondary)' }}>
          {freezes.map((f) => {
            const held = heldIn?.(f.targetEnv);
            return (
              <li key={`${f.product}|${f.service ?? ''}|${f.targetEnv}`} className="flex flex-wrap items-center gap-x-2">
                <span className="font-semibold" style={{ color: 'var(--text-primary)' }}>
                  {f.service ? `${f.service} → ` : ''}
                  {getDisplayName(f.targetEnv)}
                </span>
                <span>{f.reason}</span>
                <span style={{ color: 'var(--text-muted)' }}>
                  · frozen by {f.frozenBy}{' '}
                  <span title={format(new Date(f.from), 'MMM d, yyyy HH:mm')}>{since(f.from)}</span>
                  {held !== undefined && ` · ${held} ${held === 1 ? 'promotion' : 'promotions'} held`}
                </span>
              </li>
            );
          })}
        </ul>
        <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
          Promotions wait until the freeze is lifted. Deploys that still land meanwhile are marked
          with an amber snowflake.
        </p>
      </div>
    </div>
  );
}

/** Snowflake next to an environment's name (matrix column, environment card) while it is frozen. */
export function FrozenEnvMark({ freeze, envLabel }: { freeze: PromotionFreezeWindow; envLabel: string }) {
  const label =
    `Promotions to ${envLabel} are frozen for ${freezeScope(freeze)}: ${freeze.reason}. ` +
    `Frozen by ${freeze.frozenBy} ${since(freeze.from)}.`;
  return (
    <span className="inline-flex" title={label} aria-label={label} style={{ color: 'var(--info)' }}>
      <Snowflake size={12} />
    </span>
  );
}

/**
 * "This deploy landed while its environment was frozen for its product." An amber snowflake, or a
 * labelled badge where there is room (detail page, environment card).
 */
export function DuringFreezeMark({
  freeze,
  envLabel,
  variant = 'icon',
}: {
  freeze: PromotionFreezeWindow;
  envLabel: string;
  variant?: 'icon' | 'badge';
}) {
  const label =
    `Deployed while promotions to ${envLabel} were frozen for ${freezeScope(freeze)}: ${freeze.reason}. ` +
    `Frozen by ${freeze.frozenBy}` +
    (freeze.until
      ? `, lifted${freeze.liftedBy ? ` by ${freeze.liftedBy}` : ''} ${since(freeze.until)}.`
      : `, still frozen.`);
  if (variant === 'badge') {
    return (
      <span
        className="badge"
        title={label}
        style={{ backgroundColor: 'var(--warning-bg)', color: 'var(--warning)' }}
      >
        <Snowflake size={10} /> During freeze
      </span>
    );
  }
  return (
    <span className="inline-flex" title={label} aria-label={label} style={{ color: 'var(--warning)' }}>
      <Snowflake size={12} />
    </span>
  );
}
