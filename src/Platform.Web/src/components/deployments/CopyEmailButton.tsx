import { useState } from 'react';
import { Copy, Check } from 'lucide-react';

interface Props {
  email?: string | null;
  size?: number;
}

/**
 * Tiny icon button that copies a participant's email to the clipboard.
 * Renders nothing when the email is missing. Falls back to a legacy
 * execCommand path when the async clipboard API isn't available
 * (e.g. non-HTTPS contexts).
 */
export function CopyEmailButton({ email, size = 12 }: Props) {
  const [copied, setCopied] = useState(false);

  if (!email) return null;

  const handleCopy = async (e: React.MouseEvent) => {
    e.stopPropagation();
    e.preventDefault();
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(email);
      } else {
        const ta = document.createElement('textarea');
        ta.value = email;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
      }
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // swallow — surface nothing, user can retry
    }
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title={copied ? 'Copied!' : `Copy ${email}`}
      aria-label={copied ? 'Email copied' : `Copy email ${email}`}
      className="inline-flex items-center justify-center p-0.5 rounded transition-colors hover:opacity-80"
      style={{ color: copied ? 'var(--success, #22c55e)' : 'var(--text-muted)' }}
    >
      {copied ? <Check size={size} /> : <Copy size={size} />}
    </button>
  );
}
