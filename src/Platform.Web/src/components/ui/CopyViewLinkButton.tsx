import { useState } from 'react';
import { Check, Link as LinkIcon } from 'lucide-react';

/**
 * Copies a link to the list view on screen — the tab and every filter travel with it, so the
 * recipient opens what the sender was looking at rather than their own saved view.
 *
 * Takes the parameters rather than reading `window.location`: pages write their filters to the
 * address bar on the first change, so before that the URL is bare, and a share button that is right
 * most of the time is worse than one that is always right. Callers pass the params built from their
 * current state (see e.g. `promotionFilterParams`).
 */
export function CopyViewLinkButton({
  params,
  title = 'Copy a link to this view — the tab and every filter travel with it',
}: {
  params: URLSearchParams;
  title?: string;
}) {
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    const query = params.toString();
    const href = `${window.location.origin}${window.location.pathname}${query ? `?${query}` : ''}`;
    try {
      await navigator.clipboard.writeText(href);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard denied (insecure context, or the user said no). The same link is in the address bar
      // once any filter has been touched, so there's nothing worth an error banner here.
    }
  };

  return (
    <button
      type="button"
      onClick={() => void copy()}
      className="shrink-0 inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-[12px] font-medium transition-opacity hover:opacity-80"
      style={{
        borderColor: copied ? 'var(--success)' : 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
        color: copied ? 'var(--success)' : 'var(--text-secondary)',
      }}
      title={title}
    >
      {copied ? <Check size={12} /> : <LinkIcon size={12} />}
      {copied ? 'Link copied' : 'Copy link'}
    </button>
  );
}
