import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import type { AgentCard } from '@/lib/types';

export interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  timestamp: number;
  suggestedSlug?: string;
  /** Agent-suggested field values to pre-fill forms */
  fieldSuggestions?: Record<string, unknown>;
  /** Structured data cards for rich rendering */
  cards?: AgentCard[];
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

  // Actions
  addMessage: (msg: Omit<ChatMessage, 'timestamp'>) => void;
  replaceLoading: (msg: Omit<ChatMessage, 'timestamp'>) => void;
  setContext: (ctx: Partial<ConversationContext>) => void;
  updateFormData: (key: string, value: unknown) => void;
  setSidebarOpen: (open: boolean) => void;
  toggleSidebar: () => void;
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
      toggleSidebar: () => set((state) => ({ sidebarOpen: !state.sidebarOpen })),

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
      }),
    }
  )
);
