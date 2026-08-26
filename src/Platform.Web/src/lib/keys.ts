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
  | 'reject'
  | 'issue'
  | 'block';

/** Selector matching a control tagged for the given row action. */
export function rowActionSelector(action: RowAction): string {
  return `[${ROW_ACTION_ATTR}="${action}"]`;
}

/** Activates a tagged control, if it is present and enabled. */
function activate(scope: ParentNode, action: RowAction): boolean {
  const control = scope.querySelector<HTMLElement>(rowActionSelector(action));
  if (!control) return false;
  if (control instanceof HTMLButtonElement && control.disabled) return false;
  if (control.getAttribute('aria-disabled') === 'true') return false;

  // Anchors are opened with `window.open` rather than a synthetic click. A synthetic `.click()` on an
  // `<a target="_blank">` is honoured inconsistently — some browsers treat the programmatic click as
  // untrusted and open a background tab or nothing at all — whereas `window.open` inside a keydown
  // handler is a plain user-gesture navigation, and `.focus()` on the result brings the tab forward.
  // `noopener` is kept because the markup carries `rel="noopener"`, and dropping it here would
  // quietly hand the opened page a reference to this one.
  if (control instanceof HTMLAnchorElement && control.target === '_blank') {
    const opened = window.open(control.href, '_blank', 'noopener');
    opened?.focus();
    return true;
  }

  control.click();
  return true;
}

/**
 * Runs `action` on the row the user is on, falling back to the page.
 *
 * Two levels, because a page can have both. A promotion's detail page carries a list of work items
 * *and* its own Approve button: scoping only to the focused row meant `A` searched a work-item card,
 * found no approve control, and silently did nothing — the page-level button was right there.
 *
 * The page-level fallback fires only when the page offers exactly one control for the action. That is
 * what keeps it safe on a list: if approve controls existed on every row, a document-wide lookup would
 * have to guess which row was meant, so instead it refuses. One match is unambiguous; several is a
 * question this function has no business answering.
 *
 * Returns whether anything ran, so a caller can stay quiet rather than pretend.
 */
export function invokeRowAction(row: HTMLElement | null, action: RowAction): boolean {
  if (row && activate(row, action)) return true;
  const candidates = document.querySelectorAll(rowActionSelector(action));
  if (candidates.length === 1) return activate(document, action);
  return false;
}
