import { HelpCircle } from 'lucide-react';
import { useConversationStore } from '@/stores/conversationStore';
import { getAssistantName } from '@/lib/runtimeConfig';

/** A value a page can describe itself with. Empty and nullish entries are dropped. */
type ContextValue = string | number | boolean | null | undefined;

interface Props {
  /**
   * The page or panel's human name, as it appears on screen. Used to phrase the question so the
   * chat transcript reads like something a person asked.
   */
  page: string;
  /**
   * What the user is looking at right now — the applied filters, the record in front of them, its
   * status. Read at click time, so the values are whatever is on screen at that moment.
   *
   * Sent to the assistant as structured context rather than folded into the question: the model
   * gets the precise situation, while the transcript still reads like something a person would say.
   * Naming the values in the sentence produces lines like "I'm looking at products 0".
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
 * Asks the assistant what to do here, with no typing.
 *
 * The walkthroughs and knowledge base only pay off if people find them, and nobody discovers a
 * feature by guessing that a chat panel knows about it. This puts the question one click from the
 * thing it is about, and builds that question from what is on screen — the backend resolves the
 * route to the guides authored for it, and the page state narrows the answer to the user's actual
 * situation rather than the page in the abstract.
 */
export function HelpButton({ page, context, question, size = 'md', className = '' }: Props) {
  const askAssistant = useConversationStore((s) => s.askAssistant);
  const assistantName = getAssistantName();
  const px = size === 'sm' ? 13 : 15;

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

/**
 * Drops entries the page could not fill in. A filter nobody set and a field nobody typed are not
 * context — sending them as empty strings would have the assistant explain absences that are merely
 * defaults.
 *
 * A zero is kept, because "no products yet" and "no policies configured" are exactly the situations
 * where someone reaches for Help.
 */
function cleanContext(context?: Record<string, ContextValue>): Record<string, string> | undefined {
  if (!context) return undefined;

  const cleaned: Record<string, string> = {};
  for (const [key, value] of Object.entries(context)) {
    if (value === null || value === undefined || value === '') continue;
    cleaned[key] = String(value);
  }

  return Object.keys(cleaned).length > 0 ? cleaned : undefined;
}
