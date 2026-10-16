export interface Box {
  top: number;
  left: number;
  width: number;
  height: number;
}

/**
 * The part of an element that can actually be seen: its bounding box clipped by every scrolling
 * ancestor and by the viewport. Null when nothing of it is visible.
 *
 * Overlays that ring page elements are `position: fixed` and draw from `getBoundingClientRect()`,
 * which happily reports a box above the top bar for a row that has been scrolled out of its table.
 * The ring would then sit over the header, pointing at nothing. Clipping to the scroll container
 * keeps it where the eye can follow — shrinking as the target slides out, gone once it has.
 */
export function visibleRect(el: HTMLElement): { rect: Box; clip: Box } | null {
  let top = 0;
  let left = 0;
  let right = window.innerWidth;
  let bottom = window.innerHeight;

  for (let node = el.parentElement; node; node = node.parentElement) {
    const style = window.getComputedStyle(node);
    if (!clips(style.overflowY) && !clips(style.overflowX)) continue;
    const r = node.getBoundingClientRect();
    if (clips(style.overflowY)) {
      top = Math.max(top, r.top);
      bottom = Math.min(bottom, r.bottom);
    }
    if (clips(style.overflowX)) {
      left = Math.max(left, r.left);
      right = Math.min(right, r.right);
    }
  }

  const r = el.getBoundingClientRect();
  const t = Math.max(r.top, top);
  const l = Math.max(r.left, left);
  const b = Math.min(r.bottom, bottom);
  const rt = Math.min(r.right, right);

  if (b - t <= 0 || rt - l <= 0) return null;

  return {
    rect: { top: t, left: l, width: rt - l, height: b - t },
    clip: { top, left, width: right - left, height: bottom - top },
  };
}

function clips(overflow: string): boolean {
  return overflow === 'auto' || overflow === 'scroll' || overflow === 'hidden' || overflow === 'clip';
}
