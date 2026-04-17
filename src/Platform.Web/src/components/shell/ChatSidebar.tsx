import { useState, useRef, useEffect } from 'react';
import { useNavigate, useLocation, Link } from 'react-router-dom';
import { Send, Loader2, Sparkles, ArrowRight, X, RotateCcw, Bell, Maximize2, Minimize2 } from 'lucide-react';
import { useConversationStore } from '@/stores/conversationStore';
import { ChatCard } from '@/components/chat/ChatCard';
import { ChatInlineForm } from '@/components/chat/ChatInlineForm';
import { buildAgentUrl, getAssistantName } from '@/lib/runtimeConfig';

export function ChatSidebar() {
  const {
    threadId,
    messages,
    context,
    sidebarOpen,
    sidebarExpanded,
    addMessage,
    replaceLoading,
    setContext,
    setSidebarOpen,
    toggleSidebarExpanded,
    startNewThread,
    getHistoryForAgent,
  } = useConversationStore();

  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const location = useLocation();
  const assistantName = getAssistantName();

  // Auto-scroll on new messages
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Focus input when sidebar opens
  useEffect(() => {
    if (sidebarOpen) {
      setTimeout(() => inputRef.current?.focus(), 150);
    }
  }, [sidebarOpen]);

  // Clear catalog-specific context when the user navigates away from a catalog form.
  // Without this, a stale catalogSlug leaks into subsequent requests on other pages.
  const slugMatch = location.pathname.match(/^\/catalog\/([^/]+)$/);
  useEffect(() => {
    if (!slugMatch) {
      setContext({ catalogSlug: undefined, formData: undefined, step: undefined });
    }
  }, [location.pathname]); // eslint-disable-line react-hooks/exhaustive-deps

  // Derive current slug from the URL only — never fall back to stale store value.
  const currentSlug = slugMatch?.[1];

  const sendMessage = async (overrideMessage?: string) => {
    const msg = overrideMessage || input.trim();
    if (!msg || loading) return;

    if (!overrideMessage) {
      addMessage({ role: 'user', text: msg });
      setInput('');
    }

    setLoading(true);
    addMessage({ role: 'assistant', text: '', isLoading: true });

    try {
      const res = await fetch(buildAgentUrl('/catalog/chat'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          threadId,
          message: msg,
          pageContext: {
            currentPath: location.pathname,
            currentSlug: currentSlug || undefined,
            formData: currentSlug ? (context.formData || undefined) : undefined,
          },
          history: getHistoryForAgent(),
        }),
      });

      const data = await res.json();
      const reply = data.reply || 'No response.';

      // Check for field suggestions in the reply
      let fieldSuggestions: Record<string, unknown> | undefined;
      if (data.fieldSuggestions) {
        fieldSuggestions = data.fieldSuggestions;
        // Auto-update form data with suggestions
        setContext({
          formData: { ...(context.formData || {}), ...data.fieldSuggestions },
        });
      }

      // If validation passed, move to review step
      if (data.validationResults?.isValid) {
        setContext({ step: 'review' });
      }

      // If agent returned validation errors, update form context
      if (data.validationResults?.results) {
        // Errors will be picked up by RequestPage via the store
      }

      replaceLoading({
        role: 'assistant',
        text: reply,
        suggestedSlug: data.suggestedSlug || undefined,
        fieldSuggestions,
        cards: data.cards || undefined,
        a2uiSurface: data.a2uiSurface || undefined,
      });

      // If a service was suggested, set it in context
      if (data.suggestedSlug) {
        setContext({ catalogSlug: data.suggestedSlug, step: 'discovery' });
      }
    } catch {
      replaceLoading({
        role: 'assistant',
        text: 'Failed to reach the assistant. Please try again.',
      });
    } finally {
      setLoading(false);
    }
  };

  const handleNavigate = (slug: string) => {
    setContext({ catalogSlug: slug, step: 'form' });
    navigate(`/catalog/${slug}`);
  };

  if (!sidebarOpen) return null;

  return (
    <div
      className="flex flex-col border-l h-full"
      style={{
        ...(sidebarExpanded
          ? { flex: 1, minWidth: 0 }
          : { width: 380, minWidth: 380, flex: 'none' }),
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
      }}
    >
      {/* Header */}
      <div
        className="flex items-center justify-between px-4 py-3 border-b shrink-0"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <div className="flex items-center gap-2">
          <div
            className="w-7 h-7 rounded-full flex items-center justify-center"
            style={{ backgroundColor: 'var(--accent)', color: 'white' }}
          >
            <Sparkles size={14} />
          </div>
          <div>
            <h2 className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>
              {assistantName}
            </h2>
          </div>
        </div>
        <div className="flex items-center gap-1">
          <button
            onClick={startNewThread}
            className="p-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
            style={{ color: 'var(--text-muted)' }}
            title="New conversation"
          >
            <RotateCcw size={14} />
          </button>
          <button
            onClick={toggleSidebarExpanded}
            className="p-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
            style={{ color: 'var(--text-muted)' }}
            title={sidebarExpanded ? 'Collapse to sidebar' : 'Expand to full view'}
          >
            {sidebarExpanded ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
          </button>
          <button
            onClick={() => setSidebarOpen(false)}
            className="p-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
            style={{ color: 'var(--text-muted)' }}
          >
            <X size={16} />
          </button>
        </div>
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto px-4 py-3 space-y-3">
        {messages.map((msg, i) => (
          <div key={`${msg.timestamp}-${i}`}>
            <div
              className={`text-sm p-3 rounded-xl ${msg.role === 'user' ? 'ml-6' : 'mr-2'}`}
              style={{
                backgroundColor:
                  msg.isNotification
                    ? 'transparent'
                    : msg.role === 'assistant'
                      ? 'var(--bg-secondary)'
                      : 'var(--accent)',
                color: msg.role === 'assistant' ? 'var(--text-primary)' : 'white',
                ...(msg.isNotification
                  ? { borderLeft: '3px solid var(--accent)', paddingLeft: 12, backgroundColor: 'var(--bg-secondary)' }
                  : {}),
              }}
            >
              {msg.isLoading ? (
                <span className="flex items-center gap-2">
                  <Loader2 size={14} className="animate-spin" /> Thinking...
                </span>
              ) : (
                <span className="whitespace-pre-wrap">
                  {msg.isNotification && <Bell size={12} className="inline mr-1.5" style={{ color: 'var(--accent)' }} />}
                  <MessageText text={msg.text} />
                </span>
              )}
            </div>

            {/* Inline form rendered by the generate_form tool */}
            {msg.a2uiSurface && (
              <ChatInlineForm
                surfaceJson={msg.a2uiSurface}
                initialValues={msg.fieldSuggestions}
              />
            )}

            {/* Structured data cards */}
            {msg.cards && msg.cards.length > 0 && (
              <div className="mr-2">
                {msg.cards.map((card, ci) => (
                  <ChatCard key={ci} card={card} />
                ))}
              </div>
            )}

            {/* Service suggestion action */}
            {msg.suggestedSlug && (
              <button
                onClick={() => handleNavigate(msg.suggestedSlug!)}
                className="mt-2 flex items-center gap-1.5 text-xs font-semibold px-3 py-2 rounded-lg border transition-all hover:shadow-md"
                style={{
                  borderColor: 'var(--accent)',
                  color: 'var(--accent)',
                  backgroundColor: 'var(--bg-primary)',
                }}
              >
                Open request form <ArrowRight size={12} />
              </button>
            )}
          </div>
        ))}
        <div ref={messagesEndRef} />
      </div>

      {/* Quick suggestions (only when few messages) */}
      {messages.length <= 2 && (
        <div className="px-4 pb-2 shrink-0">
          <div className="flex flex-wrap gap-1.5">
            {[
              'What services are available?',
              'I need to create a repo',
              'Request DNS changes',
              'I need access to a resource',
            ].map((q) => (
              <button
                key={q}
                onClick={() => {
                  addMessage({ role: 'user', text: q });
                  sendMessage(q);
                }}
                className="text-xs px-2.5 py-1.5 rounded-full border transition-colors hover:bg-[var(--bg-secondary)]"
                style={{
                  borderColor: 'var(--border-color)',
                  color: 'var(--text-secondary)',
                }}
              >
                {q}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Input */}
      <div className="px-4 py-3 border-t shrink-0" style={{ borderColor: 'var(--border-color)' }}>
        <div className="flex gap-2">
          <input
            ref={inputRef}
            type="text"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && sendMessage()}
            placeholder={currentSlug ? `Ask about ${currentSlug}...` : 'Ask anything...'}
            disabled={loading}
            className="flex-1 px-3 py-2 text-sm rounded-lg border outline-none transition-colors focus:border-[var(--accent)]"
            style={{
              backgroundColor: 'var(--bg-secondary)',
              borderColor: 'var(--border-color)',
              color: 'var(--text-primary)',
            }}
          />
          <button
            onClick={() => sendMessage()}
            disabled={loading || !input.trim()}
            className="px-3 py-2 rounded-lg transition-colors disabled:opacity-40"
            style={{ backgroundColor: 'var(--accent)', color: 'white' }}
          >
            {loading ? <Loader2 size={16} className="animate-spin" /> : <Send size={16} />}
          </button>
        </div>
      </div>
    </div>
  );
}

/**
 * Renders message text with clickable links.
 * Supports: [label](url), bare /path?query URLs, and **bold**.
 */
function MessageText({ text }: { text: string }) {
  // Match markdown links [text](url), bare internal paths (/something...), and **bold**
  const parts = text.split(/(\[.+?\]\(.+?\)|\*\*.+?\*\*|(?:^|\s)(\/[a-zA-Z0-9/_-]+(?:\?[^\s)]*)?))/).filter(Boolean);

  // Simpler approach: use a single regex to find all special segments
  const segments: Array<{ type: 'text' | 'md-link' | 'bare-link' | 'bold'; value: string; label?: string; href?: string }> = [];
  const regex = /\[([^\]]+)\]\(([^)]+)\)|\*\*(.+?)\*\*|(\/deployments\/[^\s)]+|\/catalog\/[^\s)]+|\/requests\/[^\s)]+|\/settings[^\s)]*)/g;
  let lastIndex = 0;
  let match;

  while ((match = regex.exec(text)) !== null) {
    if (match.index > lastIndex) {
      segments.push({ type: 'text', value: text.slice(lastIndex, match.index) });
    }

    if (match[1] && match[2]) {
      // Markdown link: [label](url)
      segments.push({ type: 'md-link', value: match[0], label: match[1], href: match[2] });
    } else if (match[3]) {
      // Bold: **text**
      segments.push({ type: 'bold', value: match[3] });
    } else if (match[4]) {
      // Bare internal path
      segments.push({ type: 'bare-link', value: match[4], href: match[4] });
    }

    lastIndex = match.index + match[0].length;
  }

  if (lastIndex < text.length) {
    segments.push({ type: 'text', value: text.slice(lastIndex) });
  }

  if (segments.length === 0) return <>{text}</>;

  return (
    <>
      {segments.map((seg, i) => {
        if (seg.type === 'text') return <span key={i}>{seg.value}</span>;
        if (seg.type === 'bold') return <strong key={i}>{seg.value}</strong>;

        const href = seg.href!;
        const label = seg.label ?? href;
        const isInternal = href.startsWith('/');

        if (isInternal) {
          return (
            <Link
              key={i}
              to={href}
              className="underline font-medium hover:opacity-80"
              style={{ color: 'var(--accent)' }}
              onClick={(e) => e.stopPropagation()}
            >
              {label}
            </Link>
          );
        }

        return (
          <a
            key={i}
            href={href}
            target="_blank"
            rel="noopener noreferrer"
            className="underline font-medium hover:opacity-80"
            style={{ color: 'var(--accent)' }}
            onClick={(e) => e.stopPropagation()}
          >
            {label}
          </a>
        );
      })}
    </>
  );
}
