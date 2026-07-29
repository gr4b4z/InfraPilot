import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useHotkeys } from '@/hooks/useHotkeys';
import { activeKeyboardRow } from '@/hooks/keyboardList';
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

  // An open dialog owns the keyboard — otherwise the palette's accelerators would double as
  // navigation, and `d` would fire twice.
  const modalOpen = paletteOpen || quickFindOpen || helpOpen;

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
      ':': () => setPaletteOpen(true),
      '/': () => setQuickFindOpen(true),
      '?': () => setHelpOpen(true),
      Escape: escape,

      // Row actions. `a` is assign, `A` approve — distinct bindings, which is why matching is
      // case-sensitive.
      o: () => act('open-external'),
      a: () => act('assign'),
      A: () => act('approve'),
      R: () => act('reject'),
      I: () => act('issue'),
      B: () => act('block'),
    },
    { enabled: !modalOpen },
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
