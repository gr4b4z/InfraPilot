import { Bell, Monitor, Moon, Sun, Sparkles } from 'lucide-react';
import { useState, useEffect } from 'react';
import { useConversationStore } from '@/stores/conversationStore';
import { useAuthStore } from '@/stores/authStore';

type ThemeMode = 'light' | 'dark' | 'system';

const THEME_STORAGE_KEY = 'theme-mode';

export function Topbar() {
  const { sidebarOpen, toggleSidebar } = useConversationStore();
  const user = useAuthStore((s) => s.user);

  const [themeMode, setThemeMode] = useState<ThemeMode>(() => {
    if (typeof window !== 'undefined') {
      const storedTheme = window.localStorage.getItem(THEME_STORAGE_KEY);
      if (storedTheme === 'light' || storedTheme === 'dark' || storedTheme === 'system') {
        return storedTheme;
      }
    }
    return 'system';
  });
  const [systemPrefersDark, setSystemPrefersDark] = useState(() =>
    typeof window !== 'undefined'
      ? window.matchMedia('(prefers-color-scheme: dark)').matches
      : false,
  );

  const darkMode = themeMode === 'system' ? systemPrefersDark : themeMode === 'dark';

  useEffect(() => {
    const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    const updateSystemPreference = (event: MediaQueryListEvent) => {
      setSystemPrefersDark(event.matches);
    };

    setSystemPrefersDark(mediaQuery.matches);
    mediaQuery.addEventListener('change', updateSystemPreference);
    return () => mediaQuery.removeEventListener('change', updateSystemPreference);
  }, []);

  useEffect(() => {
    const root = document.documentElement;

    root.classList.remove('light', 'dark');

    if (themeMode === 'light') {
      root.classList.add('light');
    } else if (themeMode === 'dark') {
      root.classList.add('dark');
    }

    root.style.colorScheme = darkMode ? 'dark' : 'light';
    window.localStorage.setItem(THEME_STORAGE_KEY, themeMode);
  }, [themeMode, darkMode]);

  const themeLabel = themeMode === 'system'
    ? `System (${darkMode ? 'dark' : 'light'})`
    : themeMode === 'dark'
      ? 'Always dark'
      : 'Always light';

  // Keyboard shortcut: Cmd+K / Ctrl+K
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault();
        toggleSidebar();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [toggleSidebar]);

  return (
    <header
      className="flex items-center h-14 px-6 border-b gap-4 shrink-0"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
      }}
    >
      {/* AI command bar */}
      <button
        onClick={toggleSidebar}
        className="flex items-center flex-1 max-w-lg gap-2.5 px-3 py-[7px] rounded-lg cursor-pointer transition-all duration-150"
        style={{
          backgroundColor: sidebarOpen ? 'var(--accent-muted)' : 'var(--bg-secondary)',
          border: `1px solid ${sidebarOpen ? 'var(--accent)' : 'var(--border-color)'}`,
        }}
      >
        <Sparkles size={14} style={{ color: 'var(--accent)' }} />
        <span className="flex-1 text-left text-[13px]" style={{ color: 'var(--text-muted)' }}>
          Ask AI assistant or search...
        </span>
        <kbd
          className="hidden sm:inline-flex items-center gap-0.5 px-1.5 py-0.5 rounded text-[10px] font-mono font-medium"
          style={{
            backgroundColor: 'var(--bg-primary)',
            color: 'var(--text-muted)',
            border: '1px solid var(--border-color)',
          }}
        >
          {navigator.platform.includes('Mac') ? '⌘' : 'Ctrl'}K
        </kbd>
      </button>

      {/* Right actions */}
      <div className="flex items-center gap-1 ml-auto">
        <div className="flex items-center gap-1 px-1 py-1 rounded-lg" style={{ backgroundColor: 'var(--bg-secondary)' }}>
          <button
            onClick={() => setThemeMode('light')}
            className="p-2 rounded-md transition-colors"
            style={{
              color: themeMode === 'light' ? 'white' : 'var(--text-muted)',
              backgroundColor: themeMode === 'light' ? 'var(--accent)' : 'transparent',
            }}
            title="Always light"
            aria-pressed={themeMode === 'light'}
          >
            <Sun size={16} />
          </button>
          <button
            onClick={() => setThemeMode('dark')}
            className="p-2 rounded-md transition-colors"
            style={{
              color: themeMode === 'dark' ? 'white' : 'var(--text-muted)',
              backgroundColor: themeMode === 'dark' ? 'var(--accent)' : 'transparent',
            }}
            title="Always dark"
            aria-pressed={themeMode === 'dark'}
          >
            <Moon size={16} />
          </button>
          <button
            onClick={() => setThemeMode('system')}
            className="p-2 rounded-md transition-colors"
            style={{
              color: themeMode === 'system' ? 'white' : 'var(--text-muted)',
              backgroundColor: themeMode === 'system' ? 'var(--accent)' : 'transparent',
            }}
            title={themeLabel}
            aria-pressed={themeMode === 'system'}
          >
            <Monitor size={16} />
          </button>
        </div>

        <button
          className="p-2 rounded-lg transition-colors hover:bg-[var(--bg-secondary)] relative"
          style={{ color: 'var(--text-muted)' }}
          title="Notifications"
        >
          <Bell size={16} />
          {/* Notification dot */}
          <span
            className="absolute top-1.5 right-1.5 w-2 h-2 rounded-full"
            style={{ backgroundColor: 'var(--danger)' }}
          />
        </button>

        <div className="w-px h-6 mx-1.5" style={{ backgroundColor: 'var(--border-color)' }} />

        <button className="flex items-center gap-2 px-2 py-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]">
          <div
            className="flex items-center justify-center w-7 h-7 rounded-full text-[11px] font-bold text-white"
            style={{ backgroundColor: 'var(--accent)' }}
          >
            {user?.initials ?? 'DU'}
          </div>
          <span className="hidden sm:block text-[13px] font-medium" style={{ color: 'var(--text-secondary)' }}>
            {user?.name?.split(' ')[0] ?? 'Dev'}
          </span>
        </button>
      </div>
    </header>
  );
}
