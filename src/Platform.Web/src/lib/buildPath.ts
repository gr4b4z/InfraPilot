/**
 * In-app route to the build registry, filtered.
 *
 * There is no per-build page: the registry list *is* the build view, and its filters live in the
 * URL, so `(product, service, version)` — the triple a build is unique on — narrows it to the one
 * row. That is what "built from <branch>" on a promotion links to.
 */
export function buildRegistryPath(filters: {
  product?: string;
  service?: string;
  version?: string;
  branch?: string;
}): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filters)) {
    if (value) params.set(key, value);
  }
  const query = params.toString();
  return query ? `/builds?${query}` : '/builds';
}
