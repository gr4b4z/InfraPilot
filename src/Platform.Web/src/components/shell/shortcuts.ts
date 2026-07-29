import { commandKeyLabel } from '@/lib/keys';

/**
 * The app's keyboard vocabulary, in one list.
 *
 * This is the source the help overlay renders, so a shortcut that isn't described here is a shortcut
 * nobody can discover. Add the entry in the same change that adds the binding.
 */
export interface ShortcutGroup {
  title: string;
  items: Array<{ keys: string[]; description: string }>;
}

export function shortcutGroups(): ShortcutGroup[] {
  return [
    {
      title: 'Move around',
      items: [
        { keys: [':'], description: 'Go to… — opens a menu of destinations' },
        { keys: ['/'], description: 'Search whatever the current page lists' },
        { keys: ['Esc'], description: 'Close what is open, or go back' },
        { keys: [`${commandKeyLabel()}`, 'K'], description: 'Ask the AI assistant' },
        { keys: ['?'], description: 'Show this help' },
      ],
    },
    {
      title: 'Within a page',
      items: [
        { keys: ['Tab'], description: 'Next region — filters, tabs, list, actions' },
        { keys: ['↑', '↓'], description: 'Move within the focused region' },
        { keys: ['←', '→'], description: 'Move across a tab strip, or across environments in the matrix' },
        { keys: ['Home', 'End'], description: 'First / last item in the region' },
        { keys: ['Enter'], description: 'Open the focused item' },
      ],
    },
    {
      title: 'On the focused item',
      items: [
        { keys: ['o'], description: 'Open its tracker reference or pull request in a new tab' },
        { keys: ['a'], description: 'Assign or reassign someone' },
      ],
    },
    {
      title: 'Decisions',
      items: [
        { keys: ['A'], description: 'Approve — asks to confirm' },
        { keys: ['R'], description: 'Reject — asks to confirm, comment required' },
        { keys: ['I'], description: 'Raise an issue on a work item' },
        { keys: ['B'], description: 'Block a work item' },
      ],
    },
  ];
}
