import { commandKeyLabel } from '@/lib/keys';

/**
 * The app's keyboard vocabulary, in one list.
 *
 * This is the source the help overlay renders, so a shortcut that isn't described here is a
 * shortcut nobody can discover. Add the entry in the same change that adds the binding.
 *
 * Chords are written the way they're typed — `g` then `d` — because that's what the user has to do,
 * not the internal `'g d'` map key.
 */
export interface ShortcutGroup {
  title: string;
  items: Array<{ keys: string[]; description: string }>;
}

export function shortcutGroups(): ShortcutGroup[] {
  return [
    {
      title: 'Go to',
      items: [
        { keys: ['g', 'd'], description: 'Deployments' },
        { keys: ['g', 'p'], description: 'Promotions' },
        { keys: ['g', 'w'], description: 'Work items queue' },
        { keys: ['g', 't'], description: 'My tasks' },
        { keys: ['g', 'c'], description: 'Service catalog' },
        { keys: ['g', 'r'], description: 'Rollbacks' },
      ],
    },
    {
      title: 'Find and ask',
      items: [
        { keys: ['/'], description: 'Find a work item by key or title' },
        { keys: [`${commandKeyLabel()}`, 'K'], description: 'Ask the AI assistant' },
        { keys: ['?'], description: 'Show this help' },
        { keys: ['Esc'], description: 'Close a dialog, popover or drawer' },
      ],
    },
    {
      title: 'In a list',
      items: [
        { keys: ['↑', '↓'], description: 'Move between rows (or j / k)' },
        { keys: ['←', '→'], description: 'Move across environments in the deployment matrix' },
        { keys: ['Home', 'End'], description: 'First / last row' },
        { keys: ['Enter'], description: 'Open the focused row' },
        { keys: ['Tab'], description: 'Move between regions — filters, list, actions' },
      ],
    },
    {
      title: 'On the focused row or work item',
      items: [
        { keys: ['o'], description: 'Open the tracker reference (Jira, Azure DevOps) or pull request' },
        { keys: ['a'], description: 'Assign or reassign someone' },
        { keys: ['A'], description: 'Approve' },
        { keys: ['I'], description: 'Raise an issue' },
        { keys: ['B'], description: 'Block' },
      ],
    },
  ];
}
