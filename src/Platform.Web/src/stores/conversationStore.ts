import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { AgentCard } from '@/lib/types';
import type { GuidePlan } from './guideStore';

export interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  timestamp: number;
  suggestedSlug?: string;
  /** Agent-suggested field values to pre-fill forms */
  fieldSuggestions?: Record<string, unknown>;
  /** Structured data cards for rich rendering */
  cards?: AgentCard[];
  /** A2UI surface JSON emitted by the generate_form tool — renders a form inline in chat */
  a2uiSurface?: string;
  /** Walkthrough started by the start_guide tool — kept so the user can replay it later */
  guide?: GuidePlan;
  /** Whether this is an ambient notification (SSE push) */
  isNotification?: boolean;
  isLoading?: boolean;
}

export interface ConversationContext {
  catalogSlug?: string;
  formData?: Record<string, unknown>;
  step?: 'discovery' | 'form' | 'review' | 'submitted';
}

interface ConversationState {
  threadId: string;
  messages: ChatMessage[];
  context: ConversationContext;
  sidebarOpen: boolean;
  /** When true the chat takes over the full content area (main view is hidden). */
  sidebarExpanded: boolean;
  /**
   * A question queued by something outside the chat, waiting for ChatSidebar to pick it up.
   * Not persisted — a reload should not re-ask what the user asked in a previous session.
   */
  pendingQuestion: string | null;
  /** What the caller was looking at when it asked, sent alongside the question. */
  pendingPageState: Record<string, string> | null;

  // Actions
  addMessage: (msg: Omit<ChatMessage, 'timestamp'>) => void;
  replaceLoading: (msg: Omit<ChatMessage, 'timestamp'>) => void;
  setContext: (ctx: Partial<ConversationContext>) => void;
  updateFormData: (key: string, value: unknown) => void;
  setSidebarOpen: (open: boolean) => void;
  /**
   * Ask the assistant something from outside the chat — the per-page Help buttons use this.
   * Opens the panel and leaves the question for ChatSidebar to send, so callers do not need to
   * know how a turn is assembled (page context, history, auth).
   */
  askAssistant: (question: string, pageState?: Record<string, string>) => void;
  /** Takes the queued question and its context, clearing them so a re-render cannot send twice. */
  consumePendingQuestion: () => { question: string; pageState: Record<string, string> | null } | null;
  toggleSidebar: () => void;
  toggleSidebarExpanded: () => void;
  startNewThread: () => void;
  getHistoryForAgent: () => Array<{ role: string; content: string }>;
}

const generateThreadId = () =>
  `thread-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

export const useConversationStore = create<ConversationState>()(
  persist(
    (set, get) => ({
      threadId: generateThreadId(),
      messages: [
        {
          role: 'assistant' as const,
          text: "Hi! I'm your platform assistant. I can help you find services, create requests, or answer questions. What do you need?",
          timestamp: Date.now(),
        },
      ],
      context: {},
      sidebarOpen: false,
      sidebarExpanded: false,
      pendingQuestion: null,
      pendingPageState: null,

      addMessage: (msg) =>
        set((state) => ({
          messages: [...state.messages, { ...msg, timestamp: Date.now() }],
        })),

      replaceLoading: (msg) =>
        set((state) => ({
          messages: [
            ...state.messages.filter((m) => !m.isLoading),
            { ...msg, timestamp: Date.now() },
          ],
        })),

      setContext: (ctx) =>
        set((state) => ({
          context: { ...state.context, ...ctx },
        })),

      updateFormData: (key, value) =>
        set((state) => ({
          context: {
            ...state.context,
            formData: { ...(state.context.formData || {}), [key]: value },
          },
        })),

      setSidebarOpen: (open) => set({ sidebarOpen: open }),

      askAssistant: (question, pageState) =>
        set({ sidebarOpen: true, pendingQuestion: question, pendingPageState: pageState ?? null }),

      consumePendingQuestion: () => {
        const { pendingQuestion, pendingPageState } = get();
        if (!pendingQuestion) return null;
        set({ pendingQuestion: null, pendingPageState: null });
        return { question: pendingQuestion, pageState: pendingPageState };
      },
      toggleSidebar: () => set((state) => ({ sidebarOpen: !state.sidebarOpen })),
      toggleSidebarExpanded: () => set((state) => ({ sidebarExpanded: !state.sidebarExpanded })),

      startNewThread: () =>
        set({
          threadId: generateThreadId(),
          messages: [
            {
              role: 'assistant',
              text: "Hi! I'm your platform assistant. I can help you find services, create requests, or answer questions. What do you need?",
              timestamp: Date.now(),
            },
          ],
          context: {},
        }),

      getHistoryForAgent: () => {
        const { messages } = get();
        // Send last 20 messages to keep context window manageable
        return messages
          .filter((m) => !m.isLoading)
          .slice(-20)
          .map((m) => ({ role: m.role, content: m.text }));
      },
    }),
    {
      name: 'swo-conversation',
      partialize: (state) => ({
        threadId: state.threadId,
        messages: state.messages.filter((m) => !m.isLoading).slice(-50), // keep last 50
        context: state.context,
        sidebarExpanded: state.sidebarExpanded,
      }),
    }
  )
);
