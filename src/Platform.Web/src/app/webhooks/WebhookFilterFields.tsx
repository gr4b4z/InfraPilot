import type { WebhookFilters } from '@/lib/types';
import { MultiValueInput } from '@/components/ui/MultiValueInput';
import { useWebhookFilterOptions } from './webhookFilters';

/**
 * The three filter dimensions, shared by the webhook form, the notification form and the detail
 * editor. Each is a set: leaving one empty means "every value", and the dimensions are ANDed, so
 * products [billing] with environments [prod, preprod] is billing's two upper environments and
 * nothing else.
 */
export function WebhookFilterFields({
  filters,
  onChange,
  editing = false,
}: {
  filters: WebhookFilters;
  onChange: (next: WebhookFilters) => void;
  /** Adds the line about clearing, which only means something where a stored value exists. */
  editing?: boolean;
}) {
  const options = useWebhookFilterOptions();
  const set = (patch: Partial<WebhookFilters>) => onChange({ ...filters, ...patch });

  return (
    <div className="space-y-3">
      <div>
        <p className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
          Filters <span style={{ color: 'var(--text-muted)' }}>(optional)</span>
        </p>
        <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>
          {editing ? 'Clear a field to widen it back to everything. ' : ''}
          An empty field means every value. Add several to listen to more than one, and note that a
          field is only applied to events that name it — a service filter does not silence the
          product-wide events like release notes or rollbacks.
        </p>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div className="space-y-1.5">
          <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Products
          </label>
          <MultiValueInput
            values={filters.products}
            onChange={(products) => set({ products })}
            suggestions={options.products}
            placeholder="e.g. billing-platform"
            ariaLabel="Product filter"
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Services
          </label>
          <MultiValueInput
            values={filters.services}
            onChange={(services) => set({ services })}
            suggestions={options.services}
            placeholder="e.g. api"
            ariaLabel="Service filter"
          />
        </div>
        <div className="space-y-1.5">
          <label className="text-[12px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            Environments
          </label>
          <MultiValueInput
            values={filters.environments}
            onChange={(environments) => set({ environments })}
            suggestions={options.environments}
            placeholder="e.g. production"
            ariaLabel="Environment filter"
          />
        </div>
      </div>
    </div>
  );
}

/**
 * The stored filters as a compact read-only summary, for the list rows and the detail header.
 * Renders nothing at all when no dimension is set — an unfiltered subscription should not carry an
 * empty row of labels saying so.
 */
export function WebhookFilterSummary({
  filters,
  className = 'flex flex-wrap gap-1.5',
}: {
  filters: WebhookFilters;
  className?: string;
}) {
  const dimensions: [string, string[]][] = [
    ['Product', filters.products],
    ['Service', filters.services],
    ['Env', filters.environments],
  ];
  const active = dimensions.filter(([, values]) => values.length > 0);
  if (active.length === 0) return null;

  return (
    <div className={className}>
      {active.map(([label, values]) => (
        <span
          key={label}
          className="px-1.5 py-0.5 rounded text-[11px]"
          style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-muted)' }}
        >
          {label}: {values.join(', ')}
        </span>
      ))}
    </div>
  );
}
