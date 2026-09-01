import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { formatDistanceToNow } from 'date-fns';
import {
  ArrowLeft,
  ArrowRight,
  ArrowUp,
  CheckCircle,
  ChevronRight,
  Clock,
  History,
  Loader2,
  Rocket,
  SearchX,
  Undo2,
  XCircle,
} from 'lucide-react';
import { api } from '@/lib/api';
import { deploymentDetailPath } from '@/lib/deploymentPath';
import { useDocumentTitle } from '@/lib/pageTitle';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import { useSettingsStore } from '@/stores/settingsStore';
import { FeatureFlag, useFeatureFlag } from '@/stores/featureFlagsStore';
import { EnvBadge, EnvLabel } from '@/components/environments/EnvBadge';
import { DeployArtifactDialog } from '@/components/artifacts/DeployArtifactDialog';
import { ReleaseTimeline } from './ReleaseTimeline';
import { KeyboardList } from '@/components/ui/KeyboardList';
import { useKeyboardListRow } from '@/hooks/keyboardList';
import type {
  BuildTarget,
  DeploymentStateEntry,
  ServiceDetail,
  ServicePromotion,
  ServiceVersion,
} from '@/lib/types';

/**
 * The single place for one service: where it currently runs, the versions it recently shipped,
 * and the promotions moving it between environments. The flat history list stays its own page —
 * this one answers "how is checkout-api doing", not "list every deploy ever".
 */
export function ServiceDetailPage() {
  const { product, service } = useParams<{ product: string; service: string }>();
  const { getOrderedEnvironments, getDisplayName } = useSettingsStore();
  const promotionsEnabled = useFeatureFlag(FeatureFlag.Promotions);

  const [detail, setDetail] = useState<ServiceDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);

  // The build → * edges this service can deploy a registered artifact to. Empty (the default) hides
  // the "Deploy an artifact" affordance entirely — a button that always 422s is worse than no button.
  const [artifactTargets, setArtifactTargets] = useState<BuildTarget[]>([]);
  const [deployArtifactOpen, setDeployArtifactOpen] = useState(false);

  useEffect(() => {
    if (!product || !service || !promotionsEnabled) return;
    let cancelled = false;
    api
      .getBuildTargets(product, service)
      .then((r) => {
        if (!cancelled) setArtifactTargets(r.targets);
      })
      .catch(() => {
        if (!cancelled) setArtifactTargets([]);
      });
    return () => {
      cancelled = true;
    };
  }, [product, service, promotionsEnabled]);

  useDocumentTitle([`${product}/${service}`, 'Service']);

  // Deploys repaint the environment cards and version list; promotions the promotion feed.
  const deploymentsTick = useEntityRefresh(['deployment'], {
    filter: (evt) => !evt.product || evt.product === product,
  });
  const promotionsTick = useEntityRefresh(['promotion'], {
    filter: (evt) => !evt.product || evt.product === product,
  });

  useEffect(() => {
    if (!product || !service) return;
    let cancelled = false;
    api
      .getServiceDetail(product, service)
      .then((d) => {
        if (cancelled) return;
        setDetail(d);
        setFailed(false);
        setLoading(false);
      })
      .catch(() => {
        // 404 (unknown or retired service) and a transport error land the same way; the page
        // can't tell them apart from the thrown Error, and the recovery is identical: go back up.
        if (cancelled) return;
        setDetail(null);
        setFailed(true);
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [product, service, deploymentsTick, promotionsTick]);

  // Where a deploy-event detail page's back link should return to: this page.
  const backHref = `/deployments/${encodeURIComponent(product ?? '')}/${encodeURIComponent(service ?? '')}`;

  const environments = useMemo(() => {
    if (!detail) return [];
    const byEnv = new Map(detail.environments.map((e) => [e.environment, e]));
    return getOrderedEnvironments(Array.from(byEnv.keys())).map((env) => byEnv.get(env)!);
  }, [detail, getOrderedEnvironments]);

  const pendingPromotions = useMemo(
    () => (detail?.promotions ?? []).filter((p) => p.status === 'Pending' || p.status === 'Approved'),
    [detail],
  );
  const settledPromotions = useMemo(
    () => (detail?.promotions ?? []).filter((p) => p.status !== 'Pending' && p.status !== 'Approved'),
    [detail],
  );

  // The awaiting-approval cue on the environment tiles, same contract as the product matrix: a
  // Pending promotion sits on the environment it would land on. Promotions arrive newest-first,
  // so the first one seen per target environment wins.
  const pendingByTargetEnv = useMemo(() => {
    const map = new Map<string, ServicePromotion>();
    if (!promotionsEnabled) return map;
    for (const p of detail?.promotions ?? []) {
      if (p.status !== 'Pending') continue;
      if (!map.has(p.targetEnv)) map.set(p.targetEnv, p);
    }
    return map;
  }, [detail, promotionsEnabled]);

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
      </div>
    );
  }

  if (failed || !detail) {
    return (
      <div className="flex flex-col items-center justify-center py-20 text-center">
        <SearchX size={40} style={{ color: 'var(--text-muted)' }} />
        <p className="mt-3 text-sm" style={{ color: 'var(--text-muted)' }}>
          No deployments found for {product}/{service}
        </p>
        <Link
          to="/deployments"
          className="mt-2 text-sm font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--accent)' }}
        >
          Back to deployments
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Link
          to={`/deployments/${encodeURIComponent(product ?? '')}`}
          className="p-1.5 rounded-lg transition-colors hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          <ArrowLeft size={18} />
        </Link>
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            {service}
          </h1>
          <p className="text-sm mt-0.5" style={{ color: 'var(--text-muted)' }}>
            Service in{' '}
            <Link
              to={`/deployments/${encodeURIComponent(product ?? '')}`}
              className="hover:underline"
              style={{ color: 'var(--text-secondary)' }}
            >
              {product}
            </Link>
          </p>
        </div>
        <span className="ml-auto inline-flex items-center gap-2">
          {/* Deploy a registered artifact (any branch) to an enrolled env. Only rendered when a
             build → * policy actually resolves — see the artifactTargets fetch above. */}
          {promotionsEnabled && artifactTargets.length > 0 && (
            <button
              type="button"
              onClick={() => setDeployArtifactOpen(true)}
              className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1.5 rounded-lg transition-opacity hover:opacity-90"
              style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
            >
              <Rocket size={13} />
              Deploy an artifact
            </button>
          )}
          <Link
            to={`${backHref}/history`}
            className="inline-flex items-center gap-1.5 text-[12px] font-medium px-2.5 py-1.5 rounded-lg transition-colors hover:opacity-80"
            style={{ color: 'var(--text-muted)', border: '1px solid var(--border-color)' }}
          >
            <History size={13} />
            Full history
          </Link>
        </span>
      </div>

      {deployArtifactOpen && product && service && (
        <DeployArtifactDialog
          product={product}
          service={service}
          targets={artifactTargets}
          onClose={() => setDeployArtifactOpen(false)}
        />
      )}

      {/* ── Environments ── where the service runs right now, one card per environment. */}
      <section>
        <h2 className="text-sm font-semibold mb-2" style={{ color: 'var(--text-primary)' }}>
          Environments
        </h2>
        <KeyboardList
          className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4"
          count={environments.length}
          ariaLabel={`${service} environments`}
        >
          {environments.map((cell, index) => (
            <EnvironmentCard
              key={cell.environment}
              index={index}
              cell={cell}
              service={service ?? 'service'}
              pending={pendingByTargetEnv.get(cell.environment)}
              detailHref={deploymentDetailPath(cell.id, { path: backHref, label: service ?? 'service' })}
            />
          ))}
        </KeyboardList>
      </section>

      {/* ── Release timeline ── when versions landed where, on one shared time axis. Renders
         nothing until its own history fetch returns, so the page never blocks on it. */}
      {product && service && (
        <ReleaseTimeline
          product={product}
          service={service}
          backHref={backHref}
          refreshTick={deploymentsTick}
        />
      )}

      {/* ── Promotions ── above the version list: an open promotion is a decision somebody still
         has to make, a version list is a record. Gated exactly like everywhere else. */}
      {promotionsEnabled && (
        <section>
          <h2 className="text-sm font-semibold mb-2" style={{ color: 'var(--text-primary)' }}>
            Promotions
          </h2>
          {detail.promotions.length === 0 ? (
            <p className="text-sm" style={{ color: 'var(--text-muted)' }}>
              No promotions for this service yet
            </p>
          ) : (
            <div className="space-y-4">
              {pendingPromotions.length > 0 && (
                <PromotionGroup
                  title="Open"
                  promotions={pendingPromotions}
                  getDisplayName={getDisplayName}
                />
              )}
              {settledPromotions.length > 0 && (
                <PromotionGroup
                  title={pendingPromotions.length > 0 ? 'Recent' : undefined}
                  promotions={settledPromotions}
                  getDisplayName={getDisplayName}
                />
              )}
            </div>
          )}
        </section>
      )}

      {/* ── Recent versions ── the last distinct versions and how far each one got. */}
      <section>
        <h2 className="text-sm font-semibold mb-2" style={{ color: 'var(--text-primary)' }}>
          Recent versions
        </h2>
        {detail.recentVersions.length === 0 ? (
          <p className="text-sm" style={{ color: 'var(--text-muted)' }}>
            No versions recorded
          </p>
        ) : (
          <div className="rounded-xl border divide-y" style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}>
            {detail.recentVersions.map((v) => (
              <VersionRow
                key={v.version}
                version={v}
                orderEnvironments={getOrderedEnvironments}
                backHref={backHref}
                serviceLabel={service ?? 'service'}
              />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

// ── Environments ─────────────────────────────────────────────────

/**
 * One environment's current deployment. The card opens the deploy event behind it — same
 * navigation contract as the matrix cell on the product page. A div with an activate handler
 * rather than an anchor, because the pending-promotion chip inside is itself a link and anchors
 * don't nest; the matrix cell makes the same trade for the same reason.
 */
function EnvironmentCard({ index, cell, service, pending, detailHref }: {
  index: number;
  cell: DeploymentStateEntry;
  service: string;
  /** A Pending promotion targeting this environment, if there is one. */
  pending?: ServicePromotion;
  detailHref: string;
}) {
  const navigate = useNavigate();
  const rowProps = useKeyboardListRow(index, () => navigate(detailHref), {
    role: null,
    label:
      `${cell.environment}: v${cell.version}, ${cell.status}` +
      `${pending ? `, promotion to ${pending.version} awaiting approval` : ''}. Open deployment.`,
  });

  return (
    <div
      {...rowProps}
      className="card-hover rounded-xl border p-3 flex flex-col gap-2 transition-colors cursor-pointer"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <div className="flex items-center gap-2">
        <EnvLabel env={cell.environment} className="text-[13px] font-semibold" />
        <span className="flex-1" />
        <StatusBadge status={cell.status} />
      </div>
      <div className="flex items-center gap-2">
        <span className="font-mono text-[14px] font-medium" style={{ color: statusColor(cell.status) }}>
          v{cell.version}
        </span>
        {cell.isRollback && (
          <span
            title={cell.previousVersion ? `Rolled back from v${cell.previousVersion}` : 'Rollback'}
            className="inline-flex"
            style={{ color: 'var(--text-muted)' }}
          >
            <Undo2 size={12} />
          </span>
        )}
      </div>
      {pending && (
        <div>
          <PendingPromotionChip promotion={pending} service={service} />
        </div>
      )}
      <div className="flex items-center gap-2 text-[11px]" style={{ color: 'var(--text-muted)' }}>
        {formatDistanceToNow(new Date(cell.deployedAt), { addSuffix: true })}
        <span className="flex-1" />
        <ChevronRight size={13} />
      </div>
    </div>
  );
}

/**
 * "A promotion into this environment is waiting on someone." Same cue as the product matrix, and
 * like there it links straight to the promotion — it needs a decision, not a deployment page.
 * Hence the click-stop: the card's own activation opens the deploy event, which is not where this
 * chip is pointing.
 */
function PendingPromotionChip({ promotion, service }: { promotion: ServicePromotion; service: string }) {
  return (
    <Link
      to={`/promotions/${promotion.id}`}
      onClick={(e) => e.stopPropagation()}
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full text-[10px] font-semibold transition-opacity hover:opacity-80"
      style={{
        backgroundColor: 'var(--accent-bg)',
        color: 'var(--accent)',
        border: '1px solid color-mix(in srgb, var(--accent) 30%, transparent)',
      }}
      title={
        `Promotion awaiting approval: ${service} ${promotion.version} ` +
        `from ${promotion.sourceEnv} → ${promotion.targetEnv}`
      }
    >
      <ArrowUp size={10} />
      v{promotion.version}
    </Link>
  );
}

// ── Versions ─────────────────────────────────────────────────────

/**
 * One distinct version: its number, when it last deployed anywhere, and a chip per environment it
 * reached — each chip a link to the deploy event that put it there, coloured by that deploy's
 * outcome.
 */
function VersionRow({ version: v, orderEnvironments, backHref, serviceLabel }: {
  version: ServiceVersion;
  orderEnvironments: (envs: string[]) => string[];
  backHref: string;
  serviceLabel: string;
}) {
  const byEnv = new Map(v.environments.map((e) => [e.environment, e]));
  const envs = orderEnvironments(v.environments.map((e) => e.environment));

  return (
    <div
      className="px-3 py-2.5 flex flex-wrap items-center gap-x-3 gap-y-1.5"
      style={{ borderColor: 'var(--border-color)' }}
    >
      <span className="font-mono text-[13px] font-medium min-w-[80px]" style={{ color: 'var(--text-primary)' }}>
        v{v.version}
      </span>
      <span className="flex flex-wrap items-center gap-1.5">
        {envs.map((env) => {
          const entry = byEnv.get(env)!;
          return (
            <Link
              key={env}
              to={deploymentDetailPath(entry.eventId, { path: backHref, label: serviceLabel })}
              title={`${entry.status === 'succeeded' ? 'Deployed' : entry.status === 'failed' ? 'Failed' : 'In progress'} ${formatDistanceToNow(new Date(entry.deployedAt), { addSuffix: true })}${entry.isRollback ? ' (rollback)' : ''}`}
              className="inline-flex items-center gap-1 transition-opacity hover:opacity-80"
            >
              <EnvBadge env={env} size="xs" />
              {entry.isRollback && (
                <Undo2 size={10} style={{ color: 'var(--text-muted)' }} />
              )}
              {entry.status !== 'succeeded' && <StatusBadge status={entry.status} />}
            </Link>
          );
        })}
      </span>
      <span className="flex-1" />
      <span className="text-[12px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
        {formatDistanceToNow(new Date(v.lastDeployedAt), { addSuffix: true })}
      </span>
    </div>
  );
}

// ── Promotions ───────────────────────────────────────────────────

const PROMOTION_STATUS_CONFIG: Record<string, { icon: typeof Clock; color: string; bg: string }> = {
  Pending: { icon: Clock, color: 'var(--warning)', bg: 'var(--warning-bg)' },
  Approved: { icon: CheckCircle, color: 'var(--info)', bg: 'var(--info-bg)' },
  Deploying: { icon: Rocket, color: 'var(--accent)', bg: 'var(--accent-bg)' },
  Deployed: { icon: CheckCircle, color: 'var(--success)', bg: 'var(--success-bg)' },
  Superseded: { icon: Clock, color: 'var(--text-muted)', bg: 'var(--bg-secondary)' },
  Rejected: { icon: XCircle, color: 'var(--danger)', bg: 'var(--danger-bg)' },
};

function PromotionGroup({ title, promotions, getDisplayName }: {
  title?: string;
  promotions: ServicePromotion[];
  getDisplayName: (env: string) => string;
}) {
  return (
    <div>
      {title && (
        <h3 className="text-[12px] font-semibold uppercase tracking-wide mb-1.5" style={{ color: 'var(--text-muted)' }}>
          {title}
        </h3>
      )}
      <KeyboardList className="space-y-1.5" count={promotions.length} ariaLabel={title ? `${title} promotions` : 'Promotions'}>
        {promotions.map((p, index) => (
          <PromotionRow key={p.id} index={index} promotion={p} getDisplayName={getDisplayName} />
        ))}
      </KeyboardList>
    </div>
  );
}

function PromotionRow({ index, promotion: p, getDisplayName }: {
  index: number;
  promotion: ServicePromotion;
  getDisplayName: (env: string) => string;
}) {
  const config = PROMOTION_STATUS_CONFIG[p.status] ?? PROMOTION_STATUS_CONFIG.Pending;
  const Icon = config.icon;

  const rowProps = useKeyboardListRow(index, () => {}, {
    role: null,
    selfActivating: true,
    label: `v${p.version}: ${getDisplayName(p.sourceEnv)} to ${getDisplayName(p.targetEnv)}, ${p.status}`,
  });

  // The row's clock reads the promotion's most advanced timestamp, so "2 days ago" always refers
  // to the state the badge names rather than to when the candidate first appeared.
  const timestamp = p.deployedAt ?? p.approvedAt ?? p.createdAt;

  return (
    <Link
      {...rowProps}
      to={`/promotions/${p.id}`}
      className="card-hover rounded-lg border px-3 py-2.5 flex items-center gap-3 transition-colors"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <span className="font-mono text-[13px] font-medium min-w-[80px]" style={{ color: 'var(--text-primary)' }}>
        v{p.version}
      </span>
      <span className="inline-flex items-center gap-1.5">
        <EnvBadge env={p.sourceEnv} size="xs" />
        <ArrowRight size={12} style={{ color: 'var(--text-muted)' }} />
        <EnvBadge env={p.targetEnv} size="xs" />
      </span>
      <span
        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none"
        style={{ backgroundColor: config.bg, color: config.color }}
      >
        <Icon size={10} />
        {p.status}
      </span>
      <span className="flex-1" />
      <span className="text-[12px] whitespace-nowrap" style={{ color: 'var(--text-muted)' }}>
        {formatDistanceToNow(new Date(timestamp), { addSuffix: true })}
      </span>
      <ChevronRight size={14} style={{ color: 'var(--text-muted)', flexShrink: 0 }} />
    </Link>
  );
}

// ── Status helpers (deploy events) ───────────────────────────────

const STATUS_STYLES: Record<string, { bg: string; fg: string; label: string }> = {
  succeeded: { bg: 'rgba(34,197,94,0.12)', fg: '#22c55e', label: 'Succeeded' },
  failed: { bg: 'rgba(239,68,68,0.12)', fg: '#ef4444', label: 'Failed' },
  in_progress: { bg: 'rgba(234,179,8,0.12)', fg: '#eab308', label: 'In Progress' },
};

function StatusBadge({ status }: { status?: string }) {
  const s = STATUS_STYLES[status ?? 'succeeded'] ?? STATUS_STYLES.succeeded;
  return (
    <span
      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wide leading-none"
      style={{ backgroundColor: s.bg, color: s.fg }}
    >
      <span className="inline-block w-1.5 h-1.5 rounded-full" style={{ backgroundColor: s.fg }} />
      {s.label}
    </span>
  );
}

function statusColor(status?: string): string {
  if (status === 'failed') return '#ef4444';
  if (status === 'in_progress') return '#eab308';
  return 'var(--text-primary)';
}
