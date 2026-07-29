/**
 * In-app route to a deployment's detail page.
 *
 * A deploy event's identity is its id alone — product, service and environment are properties of the
 * event, not part of finding it — so the path needs nothing else.
 *
 * Pass `from` (and a label for it) when linking out of a list: the detail page turns the pair into a
 * back link, so a reader who clicked into a deployment from a filtered history returns to that
 * filtered history rather than to the product matrix a level up. It rides in the URL rather than in
 * router state so a refresh or a shared link keeps the trail.
 */
export function deploymentDetailPath(
  eventId: string,
  from?: { path: string; label: string },
): string {
  const base = `/deployments/events/${encodeURIComponent(eventId)}`;
  if (!from) return base;
  const params = new URLSearchParams({ from: from.path, fromLabel: from.label });
  return `${base}?${params.toString()}`;
}
