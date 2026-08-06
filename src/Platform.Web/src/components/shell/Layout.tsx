import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { ChatSidebar } from './ChatSidebar';
import { KeyboardLayer } from './KeyboardLayer';
import { useRealtimeEvents } from '@/hooks/useRealtimeEvents';
import { useIsDesktop } from '@/hooks/useMediaQuery';
import { useConversationStore } from '@/stores/conversationStore';
import { useMyTasksPolling } from '@/stores/myTasksStore';

export function Layout() {
  useRealtimeEvents();
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
      {/* First tab stop on the page: without it, reaching the content means tabbing through the
          whole sidebar on every navigation. Off-screen until focused. */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-[1200] focus:px-3 focus:py-2 focus:rounded-lg focus:text-[13px] focus:font-medium"
        style={{ backgroundColor: 'var(--accent)', color: '#fff' }}
      >
        Skip to main content
      </a>
      <KeyboardLayer />
      <Sidebar />
      <div className="flex flex-col flex-1 overflow-hidden min-w-0">
        <Topbar />
        <div className="flex flex-1 overflow-hidden">
          {!chatTakesOver && (
            <main
              id="main-content"
              // Focusable so the skip link actually lands here; not a tab stop itself.
              tabIndex={-1}
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
