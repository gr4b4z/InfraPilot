/**
 * Shared vocabulary for the app's keyboard layer.
 *
 * One module owns the two questions every binding has to answer the same way — "is the user typing
 * right now?" and "what is this shortcut called?" — so a new binding can't quietly disagree with the
 * help overlay about either.
 */

/**
 * True when the event target is somewhere the user is composing text, so a single-letter shortcut
 * must not fire.
 *
 * Without this, typing "a" into the directory search box would trigger the assign shortcut, and the
 * page would fight the person using it. `contenteditable` is included for the comment editors, and
 * `role="textbox"` for anything custom that presents itself as a text field. Escape is deliberately
 * *not* exempted here — callers that want Escape to work inside an input check that themselves,
 * because closing a dialog from its own search box is normal and wanted.
 */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (target.isContentEditable) return true;
  if (target.getAttribute('role') === 'textbox') return true;
  return false;
}

/**
 * True when a keydown carries a modifier that means it belongs to the browser or OS rather than to
 * our single-key bindings (Ctrl+K, Cmd+R, Alt+Left…). Shift is excluded on purpose: `A` and `a` are
 * two different bindings here, and Shift is how you type the first one.
 */
export function hasCommandModifier(event: KeyboardEvent): boolean {
  return event.ctrlKey || event.metaKey || event.altKey;
}

/** Platform-appropriate name for the command modifier, for hints and the help overlay. */
export function commandKeyLabel(): string {
  return typeof navigator !== 'undefined' && navigator.platform.includes('Mac') ? '⌘' : 'Ctrl';
}

/**
 * The `data-row-action` contract.
 *
 * A focused row exposes the actions it supports by tagging its own controls with this attribute.
 * The shortcut layer then finds the control inside the focused row and clicks it, rather than
 * re-implementing what the control already does. That keeps one code path per action — the button's
 * own handler, with its own disabled/permission logic — and means a row that can't be approved
 * simply has no approve control for the shortcut to find.
 */
export const ROW_ACTION_ATTR = 'data-row-action';

export type RowAction =
  /** Open the row's tracker reference (Jira, Azure DevOps…) or pull request. */
  | 'open-external'
  /** Open the assign / reassign picker for the row. */
  | 'assign'
  | 'approve'
  | 'issue'
  | 'block';

/** Selector matching a control tagged for the given row action. */
export function rowActionSelector(action: RowAction): string {
  return `[${ROW_ACTION_ATTR}="${action}"]`;
}

/**
 * Clicks the control for `action` within `scope`, if one is offered and enabled. Returns whether
 * anything was activated, so a caller can fall back or do nothing.
 */
export function invokeRowAction(scope: HTMLElement | null, action: RowAction): boolean {
  const control = scope?.querySelector<HTMLElement>(rowActionSelector(action));
  if (!control) return false;
  if (control instanceof HTMLButtonElement && control.disabled) return false;
  if (control.getAttribute('aria-disabled') === 'true') return false;
  control.click();
  return true;
}

/**
 * Resolves what an action shortcut should act on.
 *
 * A focused row wins. Otherwise the whole page is fair game *only* when the page has no rows at all —
 * a detail page, where "approve" unambiguously means the one thing on screen. On a list with rows but
 * none focused we deliberately return null: falling back to the document there would let a single `A`
 * approve whichever row happens to be first in the DOM, which is not a mistake worth enabling.
 */
export function actionScope(focusedRow: HTMLElement | null, rowSelector: string): HTMLElement | null {
  if (focusedRow) return focusedRow;
  const pageHasRows = document.querySelector(rowSelector) !== null;
  return pageHasRows ? null : document.body;
}
