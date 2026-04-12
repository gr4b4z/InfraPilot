import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { ChatSidebar } from './ChatSidebar';
import { useSseEvents } from '@/hooks/useSseEvents';

export function Layout() {
  useSseEvents();

  return (
    <div
      className="flex h-screen overflow-hidden"
      style={{ backgroundColor: 'var(--bg-primary)', color: 'var(--text-primary)' }}
    >
      <Sidebar />
      <div className="flex flex-col flex-1 overflow-hidden min-w-0">
        <Topbar />
        <div className="flex flex-1 overflow-hidden">
          <main
            className="flex-1 overflow-y-auto"
            style={{ backgroundColor: 'var(--bg-secondary)' }}
          >
            <div className="p-6 lg:p-8 max-w-[1400px]">
              <Outlet />
            </div>
          </main>
          <ChatSidebar />
        </div>
      </div>
    </div>
  );
}
