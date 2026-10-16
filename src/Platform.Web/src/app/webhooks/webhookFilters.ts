import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { WebhookFilterInput } from '@/lib/api';
import type { WebhookFilters } from '@/lib/types';

export const EMPTY_FILTERS: WebhookFilters = { products: [], services: [], environments: [] };

/**
 * The vocabulary behind the filter pickers, fetched once per mounting form. Failure is silent on
 * purpose: suggestions are a convenience and every field still accepts free text, so a form that
 * cannot reach the endpoint is inconvenient rather than broken.
 */
export function useWebhookFilterOptions(): WebhookFilters {
  const [options, setOptions] = useState<WebhookFilters>(EMPTY_FILTERS);

  useEffect(() => {
    let cancelled = false;
    api
      .getWebhookFilterOptions()
      .then((result) => {
        if (!cancelled) setOptions(result);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, []);

  return options;
}

/** Drops the empty dimensions, so an untouched create form sends no filters at all. */
export function toFilterInput(filters: WebhookFilters): WebhookFilterInput | undefined {
  const input: WebhookFilterInput = {};
  if (filters.products.length > 0) input.products = filters.products;
  if (filters.services.length > 0) input.services = filters.services;
  if (filters.environments.length > 0) input.environments = filters.environments;
  return Object.keys(input).length > 0 ? input : undefined;
}

export function hasAnyFilter(filters: WebhookFilters): boolean {
  return (
    filters.products.length > 0 ||
    filters.services.length > 0 ||
    filters.environments.length > 0
  );
}
