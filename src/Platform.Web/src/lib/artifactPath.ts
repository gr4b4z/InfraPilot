/**
 * In-app route to the artifact registry, filtered.
 *
 * There is no per-artifact page: the registry list *is* the artifact view, and its filters live in
 * the URL, so `(product, service, version)` — the triple an artifact is unique on — narrows it to
 * the one row. That is what "built from <branch>" on a promotion links to, and what a deployment
 * links to when it wants to show the artifact it shipped.
 */
export function artifactRegistryPath(filters: {
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
  return query ? `/artifacts?${query}` : '/artifacts';
}
