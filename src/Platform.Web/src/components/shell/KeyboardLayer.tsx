import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useHotkeys } from '@/hooks/useHotkeys';
import { activeKeyboardRow, KEYBOARD_ROW_SELECTOR } from '@/hooks/keyboardList';
import { actionScope, invokeRowAction, type RowAction } from '@/lib/keys';
import { QuickFind } from './QuickFind';
import { ShortcutHelp } from './ShortcutHelp';

/**
 * The app-wide keyboard bindings, mounted once by {@link Layout}.
 *
 * Row actions are delegated rather than reimplemented: the shortcut finds the focused row and clicks
 * the control the row already renders for that action (see `data-row-action` in `lib/keys`). So `A`
 * approves through exactly the same handler, permission check and disabled state as the Approve
 * button — a row the user can't approve simply has no control to find, and the shortcut does nothing
 * rather than firing a request the server would refuse.
 *
 * Ctrl/Cmd+K is deliberately absent: the topbar already owns it for the assistant, and it advertises
 * that binding in its own hint.
 */
export function KeyboardLayer() {
  const navigate = useNavigate();
  const [quickFindOpen, setQuickFindOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);

  // An open dialog owns the keyboard — otherwise typing "d" into quick-find would also navigate.
  const modalOpen = quickFindOpen || helpOpen;

  // On a list, act on the focused row. On a detail page, act on the page. Never on an unfocused list
  // — see actionScope.
  const act = (action: RowAction) =>
    invokeRowAction(actionScope(activeKeyboardRow(), KEYBOARD_ROW_SELECTOR), action);

  useHotkeys(
    {
      'g d': () => navigate('/deployments'),
      'g p': () => navigate('/promotions'),
      'g w': () => navigate('/me/work-items'),
      'g t': () => navigate('/my-tasks'),
      'g c': () => navigate('/catalog'),
      'g r': () => navigate('/rollbacks'),

      '/': () => setQuickFindOpen(true),
      '?': () => setHelpOpen(true),

      // Row actions. `a` is assign, `A` approve — distinct bindings, which is why matching is
      // case-sensitive.
      o: () => act('open-external'),
      a: () => act('assign'),
      A: () => act('approve'),
      I: () => act('issue'),
      B: () => act('block'),
    },
    { enabled: !modalOpen },
  );

  return (
    <>
      <QuickFind open={quickFindOpen} onClose={() => setQuickFindOpen(false)} />
      <ShortcutHelp open={helpOpen} onClose={() => setHelpOpen(false)} />
    </>
  );
}
