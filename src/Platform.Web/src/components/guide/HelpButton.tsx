import { HelpCircle } from 'lucide-react';
import { useConversationStore } from '@/stores/conversationStore';
import { getAssistantName } from '@/lib/runtimeConfig';

interface Props {
  /**
   * The page or panel's human name, as it appears on screen. Used to phrase the question so the
   * chat transcript reads like something a person asked.
   */
  page: string;
  /**
   * Overrides the whole question. Use for a specific control rather than a whole page — e.g. on a
   * form, "How do I fill in this rollback form?" beats "What can I do on the Rollbacks page?".
   */
  question?: string;
  /** Smaller variant, for section headings rather than the page title. */
  size?: 'sm' | 'md';
  className?: string;
}

/**
 * Asks the assistant what the user can do here.
 *
 * The walkthroughs only pay off if people find them, and nobody discovers a feature by guessing
 * that a chat panel knows about it. This puts the question one click from the thing it is about —
 * the backend resolves the current route to the guides authored for it, so the answer is specific
 * to the page rather than a generic tour.
 */
export function HelpButton({ page, question, size = 'md', className = '' }: Props) {
  const askAssistant = useConversationStore((s) => s.askAssistant);
  const assistantName = getAssistantName();

  const prompt = question ?? `What can I do on the ${page} page?`;
  const label = `Ask ${assistantName}: ${prompt}`;
  const px = size === 'sm' ? 13 : 15;

  return (
    <button
      type="button"
      onClick={() => askAssistant(prompt)}
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
