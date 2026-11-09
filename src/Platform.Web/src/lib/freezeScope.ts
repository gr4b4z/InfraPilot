/**
 * What a Promotion Freeze covers, as the UI prints it: `product` for a whole-product freeze,
 * `product/service` for a freeze on one service (GLOSSARY.md, ADR-0003).
 */
export function freezeScope(freeze: { product: string; service: string | null }): string {
  return freeze.service ? `${freeze.product}/${freeze.service}` : freeze.product;
}

/** Whether a freeze holds candidates of `service`: a whole-product freeze holds every service. */
export function freezeCovers(freeze: { service: string | null }, service: string): boolean {
  return freeze.service === null || freeze.service === service;
}
