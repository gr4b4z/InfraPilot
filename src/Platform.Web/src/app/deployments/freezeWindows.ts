import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PromotionFreezeWindow } from '@/lib/api';
import { freezeCovers } from '@/lib/freezeScope';

/*
 * Promotion Freeze periods for the deployments pages (GLOSSARY.md, ADR-0001/0002/0003). The cues
 * that render them live in FreezeCues.tsx. A window with `service: null` covers the whole product;
 * one with a service covers that service only, so the matchers below take the service in question
 * — omit it to ask about the product as a whole (whole-product freezes only).
 */

const NO_WINDOWS: PromotionFreezeWindow[] = [];

/**
 * Active and lifted freeze periods of one product. Refetched when `tick` changes — freeze and lift
 * broadcast as `promotion` events, so the promotion refresh tick is the one to pass. Empty while the
 * promotions feature is off, before the first answer, and when the call fails: every cue degrades to
 * not being shown.
 */
export function useFreezeWindows(product: string | undefined, enabled: boolean, tick: number) {
  const [state, setState] = useState<{ product: string; windows: PromotionFreezeWindow[] }>({
    product: '',
    windows: NO_WINDOWS,
  });
  useEffect(() => {
    if (!product || !enabled) return;
    let cancelled = false;
    api
      .listPromotionFreezeWindows(product)
      .then((r) => {
        if (!cancelled) setState({ product, windows: r.windows });
      })
      .catch(() => {
        if (!cancelled) setState({ product, windows: NO_WINDOWS });
      });
    return () => {
      cancelled = true;
    };
  }, [product, enabled, tick]);
  // Keyed by product so a navigation never paints the previous product's freezes.
  return enabled && state.product === product ? state.windows : NO_WINDOWS;
}

/** The active freezes among `windows`, i.e. the ones not lifted yet. */
export function activeFreezes(windows: PromotionFreezeWindow[]): PromotionFreezeWindow[] {
  return windows.filter((w) => w.until === null);
}

/** Whether `w` applies to `service` of its product: whole-product windows always, service ones only to theirs. */
function appliesTo(w: PromotionFreezeWindow, service: string | undefined): boolean {
  return service === undefined ? w.service === null : freezeCovers(w, service);
}

/**
 * The active freeze holding `service` (or the whole product, when omitted) on (product, env), if
 * there is one. A whole-product freeze wins over a service one when both are on.
 */
export function activeFreezeFor(
  windows: PromotionFreezeWindow[],
  product: string,
  env: string,
  service?: string,
): PromotionFreezeWindow | undefined {
  const active = windows.filter(
    (w) => w.until === null && w.product === product && w.targetEnv === env && appliesTo(w, service),
  );
  return active.find((w) => w.service === null) ?? active[0];
}

/** The freeze `service` of (product, env) was under at `at` (a deploy's `deployedAt`), if any. */
export function freezeWindowAt(
  windows: PromotionFreezeWindow[],
  product: string,
  env: string,
  at: string,
  service?: string,
): PromotionFreezeWindow | undefined {
  const t = new Date(at).getTime();
  return windows.find(
    (w) =>
      w.product === product &&
      w.targetEnv === env &&
      appliesTo(w, service) &&
      new Date(w.from).getTime() <= t &&
      (w.until === null || t < new Date(w.until).getTime()),
  );
}
