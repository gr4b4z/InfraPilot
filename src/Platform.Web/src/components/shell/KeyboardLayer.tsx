import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useHotkeys } from '@/hooks/useHotkeys';
import { activeKeyboardRow, focusIdleKeyboardList } from '@/hooks/keyboardList';
import { invokeRowAction, type RowAction } from '@/lib/keys';
import { useUiStore } from '@/stores/uiStore';
import { CommandPalette } from './CommandPalette';
import { QuickFind } from './QuickFind';
import { ShortcutHelp } from './ShortcutHelp';

/**
 * The app-wide keyboard bindings, mounted once by {@link Layout}.
 *
 * Row actions are delegated rather than reimplemented: the shortcut finds the target row and clicks
 * the control the row already renders for that action (see `data-row-action` in `lib/keys`). So `A`
 * approves through exactly the same handler, permission check and disabled state as the Approve
 * button — including the confirmation it opens — and a row that can't be approved has no control to
 * find, so the key does nothing rather than firing a request the server would refuse.
 *
 * Ctrl/Cmd+K is deliberately absent: the topbar owns it for the assistant, and advertises it there.
 */
export function KeyboardLayer() {
  const navigate = useNavigate();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [quickFindOpen, setQuickFindOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const navDrawerOpen = useUiStore((s) => s.navDrawerOpen);

  // Our own dialogs, which we can see in state.
  const ownModalOpen = paletteOpen || quickFindOpen || helpOpen;

  /**
   * Any dialog at all, including the ones we don't own — the approve/reject confirmation belongs to
   * the promotion page, and popovers belong to whichever row opened them. An open dialog has to own
   * the keyboard completely: without this, `A` inside the approve confirmation would find the
   * page-level Approve button still sitting behind the overlay and re-open the thing you are already
   * looking at, and `:` would stack a palette on top of it.
   *
   * Read from the DOM at keypress time rather than tracked in state, because there is no way for a
   * dialog owned by a page to tell this component it exists.
   */
  const dialogOpen = () => document.querySelector('[role="dialog"]') !== null;

  /** Wraps a binding so it declines while any dialog is up, leaving the key to the dialog. */
  const guard = (fn: () => unknown) => () => (dialogOpen() ? false : (fn(), true));

  // The focused row first, then the page — see invokeRowAction for why it cascades.
  const act = (action: RowAction) => invokeRowAction(activeKeyboardRow(), action);

  /**
   * Escape means "back out of where I am". Anything overlaid gets first claim — a popover, a dialog,
   * the nav drawer — and each of those closes itself on Escape already, so this only has to keep out
   * of the way. Detected from the DOM rather than from local state because the popovers belong to
   * whichever row opened them, and this component has no way to be told about those.
   *
   * With nothing overlaid it goes back in history. Not `navigate('/somewhere')`: the useful sense of
   * "back" from a work item is the queue you came from, filters and tab intact, which only history
   * knows.
   */
  const escape = useCallback(() => {
    if (document.querySelector('[role="dialog"]')) return;
    if (navDrawerOpen) return;
    navigate(-1);
  }, [navigate, navDrawerOpen]);

  useHotkeys(
    {
      // `:` opens the destination menu. The menu owns the second keystroke, so there are no chords
      // here — the accelerators live in CommandPalette alongside the list that documents them.
      ':': guard(() => setPaletteOpen(true)),
      '/': guard(() => setQuickFindOpen(true)),
      '?': guard(() => setHelpOpen(true)),
      // Not guarded: Escape checks for an open dialog itself, and must stay bound so it can fall
      // through to "go back" when nothing is open.
      Escape: escape,

      // The arrows belong to whatever region has focus, so these only fire when nothing does — see
      // focusIdleKeyboardList. Landing on the row is the whole action; the list's own handler takes
      // the next keystroke, so a second press moves as usual.
      ArrowDown: focusIdleKeyboardList,
      ArrowUp: focusIdleKeyboardList,

      // Row actions. `a` is assign, `A` approve — distinct bindings, which is why matching is
      // case-sensitive.
      o: guard(() => act('open-external')),
      a: guard(() => act('assign')),
      A: guard(() => act('approve')),
      R: guard(() => act('reject')),
      I: guard(() => act('issue')),
      B: guard(() => act('block')),
    },
    { enabled: !ownModalOpen },
  );

  return (
    <>
      {/* Mounted only while open, so its highlighted-row state starts fresh each time. */}
      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} />}
      <QuickFind open={quickFindOpen} onClose={() => setQuickFindOpen(false)} />
      <ShortcutHelp open={helpOpen} onClose={() => setHelpOpen(false)} />
    </>
  );
}
