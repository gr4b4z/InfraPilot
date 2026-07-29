import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { ChatSidebar } from './ChatSidebar';
import { useSseEvents } from '@/hooks/useSseEvents';
import { useIsDesktop } from '@/hooks/useMediaQuery';
import { useConversationStore } from '@/stores/conversationStore';
import { useMyTasksPolling } from '@/stores/myTasksStore';

export function Layout() {
  useSseEvents();
  // Feeds the sidebar counters, the topbar bell badge and the My Tasks page from one fetch.
  useMyTasksPolling();
  const { sidebarOpen, sidebarExpanded } = useConversationStore();
  const isDesktop = useIsDesktop();
  // Below `lg` there isn't room for a conversation and a table side by side, so an open chat always
  // takes the content area over — the expanded/docked distinction only exists on wide viewports.
  const chatTakesOver = sidebarOpen && (sidebarExpanded || !isDesktop);

  return (
    <div
      className="flex h-screen overflow-hidden"
      style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
    >
      <Sidebar />
      <div className="flex flex-col flex-1 overflow-hidden min-w-0">
        <Topbar />
        <div className="flex flex-1 overflow-hidden">
          {!chatTakesOver && (
            <main
              className="flex-1 overflow-y-auto"
              style={{ backgroundColor: 'var(--bg-secondary)' }}
            >
              {/* No width cap here: these are dense operational tables that should use the
                  whole viewport. Long-form pages set their own reading width instead. */}
              <div className="p-4 sm:p-6 lg:p-8">
                <Outlet />
              </div>
            </main>
          )}
          <ChatSidebar />
        </div>
      </div>
    </div>
  );
}
