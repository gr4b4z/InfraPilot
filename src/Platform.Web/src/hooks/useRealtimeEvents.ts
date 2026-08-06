import { useEffect } from 'react';
import { useAuthStore } from '@/stores/authStore';
import { useConversationStore } from '@/stores/conversationStore';
import { refreshMyTasks } from '@/stores/myTasksStore';
import { startRealtime, subscribeNotifications } from '@/lib/realtime';
import { useEntityEvent } from './useEntityEvents';

/**
 * App-level realtime wiring, mounted once in the shell Layout:
 * - opens the SignalR connection once the user is authenticated (the hub rejects anonymous
 *   connections, so connecting earlier would just burn retries),
 * - surfaces server notifications in the chat sidebar (the old SSE behaviour),
 * - keeps the my-tasks rollup fresh — one subscription feeds the sidebar counters, the topbar
 *   bell badge, MyTasksPage and MyQueuePage's tab badges.
 */
export function useRealtimeEvents(): void {
  const isAuthenticated = useAuthStore((s) => s.isAuthenticated);
  const addMessage = useConversationStore((s) => s.addMessage);

  useEffect(() => {
    if (!isAuthenticated) return;
    startRealtime();
    return subscribeNotifications((evt) => {
      if (evt.message) {
        addMessage({ role: 'assistant', text: evt.message, isNotification: true });
      }
    });
  }, [isAuthenticated, addMessage]);

  // Anything awaiting the user's action lives in these entity streams. The 60s poll in
  // useMyTasksPolling stays as the fallback for missed events.
  useEntityEvent(['promotion', 'work-item', 'approval', 'request'], () => refreshMyTasks());
}
