import { HelpCircle } from 'lucide-react';
import { useConversationStore } from '@/stores/conversationStore';
import { usePageContext, cleanContext, type ContextValue } from '@/stores/pageContextStore';
import { getAssistantName } from '@/lib/runtimeConfig';

interface Props {
  /**
   * The page or panel's human name, as it appears on screen. Used to phrase the question so the
   * chat transcript reads like something a person asked.
   */
  page: string;
  /**
   * What the user is looking at right now — the applied filters, the record in front of them, its
   * status. Published to the assistant on every turn while this button is mounted, not only when it
   * is clicked, so "how do I approve that?" typed into the chat is answered for this record.
   *
   * Sent as structured context rather than folded into the question: the model gets the precise
   * situation, while the transcript still reads like something a person would say.
   */
  context?: Record<string, ContextValue>;
  /**
   * Replaces the default question. Use where the page has a natural phrasing worth saying out loud
   * — "this promotion has been Approved for 3 hours" beats "what can I do on the Promotion page?".
   */
  question?: string;
  /** Smaller variant, for section headings rather than the page title. */
  size?: 'sm' | 'md';
  className?: string;
}

/**
 * Asks the assistant what to do here, with no typing — and, while mounted, tells the assistant what
 * this page is showing.
 *
 * The walkthroughs and knowledge base only pay off if people find them, and nobody discovers a
 * feature by guessing that a chat panel knows about it. This puts the question one click from the
 * thing it is about. The same description of the screen goes out with every typed message too, via
 * the page-context store, which is what lets the assistant resolve "this" to the record on screen.
 */
export function HelpButton({ page, context, question, size = 'md', className = '' }: Props) {
  const askAssistant = useConversationStore((s) => s.askAssistant);
  const assistantName = getAssistantName();
  const px = size === 'sm' ? 13 : 15;

  // The button is on every page that has something worth describing, so it doubles as the place
  // that describes it. A page with no Help button can call usePageContext itself.
  usePageContext(page, context);

  const asked = question ?? `What can I do on the ${page} page?`;

  const handleClick = () => askAssistant(asked, cleanContext(context));

  // Hovering shows exactly what will be asked — nothing is typed, so this is the only chance the
  // user gets to see it first.
  const label = `Ask ${assistantName}: ${asked}`;

  return (
    <button
      type="button"
      onClick={handleClick}
      data-guide-anchor="page-help-button"
      aria-label={label}
      title={label}
      className={`inline-flex items-center justify-center rounded-md p-1 shrink-0 transition-opacity hover:opacity-100 focus-visible:opacity-100 ${className}`}
      style={{ color: 'var(--text-muted)', opacity: 0.6 }}
    >
      <HelpCircle size={px} />
    </button>
  );
}
