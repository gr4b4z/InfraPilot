import { useEffect, useRef } from 'react';
import { useConversationStore } from '@/stores/conversationStore';
import { buildApiUrl } from '@/lib/runtimeConfig';

interface PlatformEvent {
  type: string;
  requestId?: string;
  serviceName?: string;
  oldStatus?: string;
  newStatus?: string;
  actorName?: string;
  message?: string;
  timestamp: string;
}

export function useSseEvents() {
  const addMessage = useConversationStore((s) => s.addMessage);
  const eventSourceRef = useRef<EventSource | null>(null);

  useEffect(() => {
    const es = new EventSource(buildApiUrl('/events/stream'));
    eventSourceRef.current = es;

    const handleEvent = (e: MessageEvent) => {
      try {
        const event: PlatformEvent = JSON.parse(e.data);
        if (event.message) {
          addMessage({
            role: 'assistant',
            text: event.message,
            isNotification: true,
          });
        }
      } catch {
        // ignore parse errors
      }
    };

    es.addEventListener('request-status-changed', handleEvent);
    es.addEventListener('approval-decision', handleEvent);

    es.onerror = () => {
      // EventSource will auto-reconnect
    };

    return () => {
      es.close();
      eventSourceRef.current = null;
    };
  }, [addMessage]);
}
