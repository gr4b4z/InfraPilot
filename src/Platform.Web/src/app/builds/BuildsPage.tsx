import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { format, formatDistanceToNow } from 'date-fns';
import { Package, Loader2, Search, X, ExternalLink } from 'lucide-react';
import { api } from '@/lib/api';
import { useDocumentTitle } from '@/lib/pageTitle';
import { BranchBadge, shortBranch } from '@/components/builds/BranchBadge';
import { ComboBox, type ComboOption } from '@/components/ui/ComboBox';
import { FilterPanel } from '@/components/ui/FilterPanel';
import { ListEmptyState, type ActiveFilterChip } from '@/components/ui/ListEmptyState';
import { RovingGroup } from '@/components/ui/RovingGroup';
import type { BuildFacets, BuildSummary } from '@/lib/types';
import {
  TIME_PRESETS,
  countActiveFilters,
  describeTimeFilter,
  isFiltered,
  parseBuildFilters,
  timeWindow,
  type BuildFilters,
} from './buildFilterParams';

/** One page of the registry. Newest first, so the cut is always "older than what you can see". */
const PAGE_SIZE = 100;

const NO_FACETS: BuildFacets = { products: [], services: [], branches: [] };

/**
 * The build registry — every published build, from any branch, newest first. This page answers
 * "what builds exist, and which branch produced them"; deploying one is the promotion surface's
 * job (plan: feature-branch-builds, Phase C), not this page's.
 *
 * Three kinds of narrowing, because people arrive with three kinds of question. The search box is
 * for a half-remembered word ("aws") and matches it as a substring across every column, so the
 * reader never has to know whether it names the product, the service or the branch. The combo boxes
 * are for the values that identify — product, service, branch — and offer what the registry
 * actually holds, with counts, because nobody remembers a feature branch's full name. The time
 * range is for "what did we build on the 14th", which no name filter can express.
 */
export function BuildsPage() {
  const [builds, setBuilds] = useState<BuildSummary[]>([]);
  const [facets, setFacets] = useState<BuildFacets>(NO_FACETS);
  const [loading, setLoading] = useState(true);
  // The filters live in the URL, not in component state, so a filtered registry is a link — which
  // is what lets a promotion point at the one build it was cut from. `replace` keeps a burst of
  // keystrokes from filling the back stack with half-typed filters.
  const [searchParams, setSearchParams] = useSearchParams();
  const filters = useMemo(() => parseBuildFilters(searchParams), [searchParams]);

  const setFilter = useCallback(
    (updates: Partial<Record<keyof BuildFilters, string>>) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          for (const [key, value] of Object.entries(updates)) {
            if (value) next.set(key, value);
            else next.delete(key);
          }
          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  const clearAll = useCallback(() => setSearchParams({}, { replace: true }), [setSearchParams]);

  const hasFilter = isFiltered(filters);
  const timeLabel = describeTimeFilter(filters);
  useDocumentTitle([filters.q.trim() || filters.product || filters.service || null, 'Builds']);

  // The one place the URL turns into an API query. Both requests take the same filters: the facet
  // counts describe the list, so they have to be asked the same question.
  const query = useMemo(
    () => ({
      q: filters.q.trim() || undefined,
      product: filters.product.trim() || undefined,
      service: filters.service.trim() || undefined,
      branch: filters.branch.trim() || undefined,
      version: filters.version.trim() || undefined,
      ...timeWindow(filters),
    }),
    [filters],
  );

  useEffect(() => {
    let cancelled = false;
    // Debounced so a keystroke burst costs one round trip, not one per letter.
    const timer = setTimeout(() => {
      Promise.all([
        api.listBuilds({ ...query, limit: PAGE_SIZE }).catch(() => ({ results: [] as BuildSummary[] })),
        // A facet failure leaves the combo boxes without suggestions — a degraded filter bar, but
        // still a usable one since the fields stay typeable — so it must not blank the list.
        api.getBuildFacets(query).catch(() => NO_FACETS),
      ]).then(([list, nextFacets]) => {
        if (cancelled) return;
        setBuilds(list.results);
        setFacets(nextFacets);
        setLoading(false);
      });
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  // Every narrowing in effect, named as its control names it — the chips the empty state shows, so
  // an empty table always says which filter emptied it and offers to undo exactly that one.
  const chipCandidates: (ActiveFilterChip | null)[] = [
    filters.q.trim() ? { label: 'Search', value: filters.q.trim(), onClear: () => setFilter({ q: '' }) } : null,
    filters.product.trim()
      ? { label: 'Product', value: filters.product, onClear: () => setFilter({ product: '' }) }
      : null,
    filters.service.trim()
      ? { label: 'Service', value: filters.service, onClear: () => setFilter({ service: '' }) }
      : null,
    filters.branch.trim()
      ? { label: 'Branch', value: filters.branch, onClear: () => setFilter({ branch: '' }) }
      : null,
    filters.version.trim()
      ? { label: 'Version', value: filters.version, onClear: () => setFilter({ version: '' }) }
      : null,
    timeLabel
      ? { label: 'Registered', value: timeLabel, onClear: () => setFilter({ time: '', from: '', to: '' }) }
      : null,
  ];
  const activeChips = chipCandidates.filter((chip): chip is ActiveFilterChip => chip !== null);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Builds
          </h1>
          <p className="text-sm mt-1" style={{ color: 'var(--text-muted)' }}>
            Registered builds from all branches — newest first
          </p>
        </div>
      </div>

      <FilterPanel activeCount={countActiveFilters(filters)}>
        <SearchInput value={filters.q} onChange={(v) => setFilter({ q: v })} />
        <ComboBox
          value={filters.product}
          onChange={(v) => setFilter({ product: v })}
          options={facetOptions(facets.products)}
          placeholder="Any product"
          ariaLabel="Product"
          clearable
          className="w-full sm:w-48"
        />
        <ComboBox
          value={filters.service}
          onChange={(v) => setFilter({ service: v })}
          options={facetOptions(facets.services)}
          placeholder="Any service"
          ariaLabel="Service"
          clearable
          className="w-full sm:w-48"
        />
        <ComboBox
          value={filters.branch}
          onChange={(v) => setFilter({ branch: v })}
          // Short names: the filter is a substring match, so `feature/MPT-1234` selects the builds
          // off `refs/heads/feature/MPT-1234` without the reader typing the ref prefix.
          options={facetOptions(facets.branches, shortBranch)}
          placeholder="Any branch"
          ariaLabel="Branch"
          clearable
          className="w-full sm:w-60"
        />
        {/* Exact, and kept in the bar rather than folded into the search box: a promotion's
           "built from …" link arrives with a version set, and a narrowing the reader can't see is
           how a one-row registry reads as a registry with one build in it. */}
        <VersionInput value={filters.version} onChange={(v) => setFilter({ version: v })} />
        <TimeRangeFilter filters={filters} onChange={setFilter} />
      </FilterPanel>

      {loading ? (
        <div className="flex items-center justify-center py-20">
          <Loader2 className="animate-spin" size={24} style={{ color: 'var(--text-muted)' }} />
        </div>
      ) : builds.length === 0 ? (
        <ListEmptyState
          icon={Package}
          tone={hasFilter ? 'filtered' : 'neutral'}
          title={hasFilter ? 'No builds match these filters' : 'No builds registered yet'}
          body={
            hasFilter
              ? 'The registry holds builds, but none under every narrowing below. Widen the time range or drop a filter.'
              : 'Publish pipelines register their builds here, so this fills up with the first publish that reports one.'
          }
          filters={activeChips}
          onClearFilters={clearAll}
        />
      ) : (
        <>
          <div
            className="rounded-xl border overflow-x-auto"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
          >
            <table className="w-full min-w-max text-[13px]">
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border-color)' }}>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Product</th>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Service</th>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Version</th>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Branch</th>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Commit</th>
                  <th className="text-left px-4 py-3 font-medium" style={{ color: 'var(--text-muted)' }}>Registered</th>
                </tr>
              </thead>
              <tbody>
                {builds.map((build) => (
                  <tr key={build.id} style={{ borderBottom: '1px solid var(--border-color)' }}>
                    <td className="px-4 py-2.5 font-medium" style={{ color: 'var(--text-primary)' }}>
                      {build.product}
                    </td>
                    <td className="px-4 py-2.5" style={{ color: 'var(--text-primary)' }}>{build.service}</td>
                    <td className="px-4 py-2.5">
                      {build.buildUrl ? (
                        <a
                          href={build.buildUrl}
                          target="_blank"
                          rel="noreferrer"
                          className="inline-flex items-center gap-1 font-mono text-[12px] hover:underline"
                          style={{ color: 'var(--accent)' }}
                          title="Open the CI run"
                        >
                          {build.version}
                          <ExternalLink size={11} />
                        </a>
                      ) : (
                        <span className="font-mono text-[12px]" style={{ color: 'var(--text-primary)' }}>
                          {build.version}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-2.5">
                      <BranchBadge branch={build.branch} />
                    </td>
                    <td className="px-4 py-2.5 font-mono text-[12px]" style={{ color: 'var(--text-muted)' }}>
                      {build.commitSha ? build.commitSha.slice(0, 8) : '—'}
                    </td>
                    {/* Relative for scanning, absolute in the tooltip — "3 days ago" is the wrong
                       unit the moment someone is checking what one specific date produced. */}
                    <td
                      className="px-4 py-2.5 whitespace-nowrap"
                      style={{ color: 'var(--text-muted)' }}
                      title={format(new Date(build.createdAt), 'PPpp')}
                    >
                      {formatDistanceToNow(new Date(build.createdAt), { addSuffix: true })}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* A capped page that says nothing reads as the whole registry, which is how someone
             concludes a build doesn't exist when it is merely older than the cut. */}
          <p className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            {builds.length < PAGE_SIZE ? countLine(builds.length, hasFilter) : capLine()}
          </p>
        </>
      )}
    </div>
  );
}

/** "3 builds match these filters" — the reading of the list the current filters produced. */
function countLine(count: number, filtered: boolean): string {
  const builds = `${count} build${count === 1 ? '' : 's'}`;
  if (!filtered) return builds;
  return `${builds} ${count === 1 ? 'matches' : 'match'} these filters`;
}

function capLine(): string {
  return `Showing the ${PAGE_SIZE} newest builds — narrow the time range or the filters to reach older ones`;
}

/** Facet values as combo-box options, each carrying how many builds picking it would show. */
function facetOptions(
  values: { value: string; count: number }[],
  display: (value: string) => string = (v) => v,
): ComboOption[] {
  return values.map((v) => ({
    value: display(v.value),
    hint: `${v.count} build${v.count === 1 ? '' : 's'}`,
  }));
}

/**
 * The free-text search. Wider than the pick lists and first in the row because it is the filter
 * that needs no knowledge of the registry's shape — the one to reach for when all you have is a
 * word from a name, a version fragment, or a commit sha off a pull request.
 */
function SearchInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <div className="relative w-full sm:w-72">
      <Search
        size={13}
        className="absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none"
        style={{ color: 'var(--text-muted)' }}
      />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Search product, service, version, branch…"
        aria-label="Search builds"
        className="w-full rounded-lg border pl-7 pr-7 py-1.5 text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Clear search"
          className="absolute right-2 top-1/2 -translate-y-1/2 transition-opacity hover:opacity-60"
          style={{ color: 'var(--text-muted)' }}
        >
          <X size={13} />
        </button>
      )}
    </div>
  );
}

/** The exact-version filter. Narrow, because a version is short and pasted rather than typed. */
function VersionInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <div className="relative w-full sm:w-44">
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Exact version"
        aria-label="Version"
        className="w-full rounded-lg border px-2.5 pr-7 py-1.5 text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
        style={{
          borderColor: 'var(--border-color)',
          backgroundColor: 'var(--bg-primary)',
          color: 'var(--text-primary)',
        }}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Clear Version"
          className="absolute right-2 top-1/2 -translate-y-1/2 transition-opacity hover:opacity-60"
          style={{ color: 'var(--text-muted)' }}
        >
          <X size={13} />
        </button>
      )}
    </div>
  );
}

/**
 * When a build was registered: presets for "recently", a date range for a specific day.
 *
 * The presets are the common case and stay one click each; the range is what answers "what did we
 * build on the 14th?", which no relative window can express. Picking "Any time" drops the bounds so
 * a stale range cannot keep filtering invisibly, and the bounds only render while the range is
 * selected, keeping the row short the rest of the time.
 */
function TimeRangeFilter({
  filters,
  onChange,
}: {
  filters: BuildFilters;
  onChange: (updates: Partial<Record<keyof BuildFilters, string>>) => void;
}) {
  return (
    <>
      <RovingGroup
        ariaLabel="Registered"
        // Scrolls rather than clipping: five presets plus their padding is a few pixels wider than
        // a 375px phone, and a half-visible "Date range" is a preset nobody finds.
        className="inline-flex max-w-full overflow-x-auto rounded-lg p-0.5 gap-0.5"
        style={{ backgroundColor: 'var(--bg-secondary)', border: '1px solid var(--border-color)' }}
      >
        {TIME_PRESETS.map((preset) => (
          <button
            key={preset.key}
            onClick={() =>
              onChange(
                preset.key === 'all'
                  ? { time: '', from: '', to: '' }
                  : { time: preset.key, ...(preset.key === 'custom' ? {} : { from: '', to: '' }) },
              )
            }
            // Read by RovingGroup to decide which control Tab lands on.
            aria-pressed={filters.time === preset.key}
            className="px-2.5 py-1.5 text-[13px] font-medium rounded-md transition-all whitespace-nowrap"
            style={{
              backgroundColor: filters.time === preset.key ? 'var(--bg-primary)' : 'transparent',
              color: filters.time === preset.key ? 'var(--text-primary)' : 'var(--text-muted)',
              boxShadow: filters.time === preset.key ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
            }}
          >
            {preset.label}
          </button>
        ))}
      </RovingGroup>
      {filters.time === 'custom' && (
        <div className="flex flex-wrap items-center gap-1.5">
          <DateTimeInput
            value={filters.from}
            onChange={(v) => onChange({ from: v })}
            ariaLabel="Registered from"
          />
          <span className="text-[12px]" style={{ color: 'var(--text-muted)' }}>
            →
          </span>
          <DateTimeInput
            value={filters.to}
            onChange={(v) => onChange({ to: v })}
            ariaLabel="Registered until"
          />
        </div>
      )}
    </>
  );
}

function DateTimeInput({
  value,
  onChange,
  ariaLabel,
}: {
  value: string;
  onChange: (v: string) => void;
  ariaLabel: string;
}) {
  return (
    <input
      type="datetime-local"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label={ariaLabel}
      title={ariaLabel}
      className="px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        color: 'var(--text-primary)',
      }}
    />
  );
}
