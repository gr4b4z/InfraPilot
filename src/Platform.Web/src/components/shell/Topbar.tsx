import { Bell, Menu, Monitor, Moon, Sun, Sparkles, LogOut } from 'lucide-react';
import { useState, useEffect, useRef } from 'react';
import { NavLink } from 'react-router-dom';
import { useConversationStore } from '@/stores/conversationStore';
import { useAuthStore } from '@/stores/authStore';
import { useMyTasksCount } from '@/stores/myTasksStore';
import { useUiStore } from '@/stores/uiStore';
import { isLocalAuthEnabled } from '@/lib/authConfig';
import { isMsalEnabled, logout as msalLogout } from '@/lib/auth';

type ThemeMode = 'light' | 'dark' | 'system';

const THEME_STORAGE_KEY = 'theme-mode';

/** Cycle order for the condensed single-button theme control shown on narrow screens. */
const THEME_CYCLE: ThemeMode[] = ['light', 'dark', 'system'];

const THEME_ICONS: Record<ThemeMode, typeof Sun> = {
  light: Sun,
  dark: Moon,
  system: Monitor,
};

export function Topbar() {
  const { sidebarOpen, toggleSidebar } = useConversationStore();
  const toggleNavDrawer = useUiStore((s) => s.toggleNavDrawer);
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const userMenuRef = useRef<HTMLDivElement>(null);
  // Promotions + work items awaiting this user. Drives the bell badge; the bell opens the
  // My Tasks page that lists exactly these items.
  const myTasksCount = useMyTasksCount();

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

  // Close user menu on outside click
  useEffect(() => {
    if (!userMenuOpen) return;
    const handleClick = (e: MouseEvent) => {
      if (userMenuRef.current && !userMenuRef.current.contains(e.target as Node))
        setUserMenuOpen(false);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [userMenuOpen]);

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

  const CycleIcon = THEME_ICONS[themeMode];

  return (
    <header
      className="flex items-center h-14 px-3 sm:px-4 lg:px-6 border-b gap-2 sm:gap-3 lg:gap-4 shrink-0"
      style={{
        borderColor: 'var(--border-color)',
        backgroundColor: 'var(--bg-primary)',
      }}
    >
      {/* Drawer trigger. Only below `lg` — above it the sidebar is always on screen. */}
      <button
        onClick={toggleNavDrawer}
        className="shrink-0 p-2 -ml-1 rounded-lg transition-colors hover:bg-[var(--bg-secondary)] lg:hidden"
        style={{ color: 'var(--text-secondary)' }}
        aria-label="Open navigation"
      >
        <Menu size={18} />
      </button>

      {/* AI command bar. Collapses to a single icon button below `sm`: the placeholder needs ~220px
          to read as a search field, and taking that from a 375px header leaves nothing for the
          account controls. */}
      <button
        onClick={toggleSidebar}
        className="flex items-center shrink-0 justify-center w-9 h-9 rounded-lg cursor-pointer transition-all duration-150 sm:shrink sm:w-auto sm:h-auto sm:flex-1 sm:max-w-lg sm:justify-start sm:gap-2.5 sm:px-3 sm:py-[7px]"
        style={{
          backgroundColor: sidebarOpen ? 'var(--accent-muted)' : 'var(--bg-secondary)',
          border: `1px solid ${sidebarOpen ? 'var(--accent)' : 'var(--border-color)'}`,
        }}
        aria-label="Ask AI assistant or search"
      >
        <Sparkles size={14} style={{ color: 'var(--accent)' }} />
        <span
          className="hidden sm:block flex-1 text-left text-[13px] truncate"
          style={{ color: 'var(--text-muted)' }}
        >
          Ask AI assistant or search...
        </span>
        <kbd
          className="hidden md:inline-flex items-center gap-0.5 px-1.5 py-0.5 rounded text-[10px] font-mono font-medium"
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
        {/* Three side-by-side modes cost ~110px, which a phone header can't spare — below `sm` the
            same three states are cycled through by one button instead. */}
        <button
          onClick={() => setThemeMode(THEME_CYCLE[(THEME_CYCLE.indexOf(themeMode) + 1) % THEME_CYCLE.length])}
          className="sm:hidden p-2 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
          style={{ color: 'var(--text-muted)' }}
          title={`Theme: ${themeLabel} — tap to change`}
          aria-label={`Theme: ${themeLabel}. Activate to change.`}
        >
          <CycleIcon size={16} />
        </button>

        <div
          className="hidden sm:flex items-center gap-1 px-1 py-1 rounded-lg"
          style={{ backgroundColor: 'var(--bg-secondary)' }}
        >
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

        {/* Bell → My tasks. The badge is a real count of things awaiting this user, so an
            empty bell renders bare rather than with a dot that means nothing. */}
        <NavLink
          to="/my-tasks"
          className="p-2 rounded-lg transition-colors hover:bg-[var(--bg-secondary)] relative"
          style={({ isActive }) => ({
            color: isActive ? 'var(--accent)' : 'var(--text-muted)',
            backgroundColor: isActive ? 'var(--accent-subtle)' : undefined,
          })}
          title={
            myTasksCount > 0
              ? `My tasks — ${myTasksCount} awaiting you`
              : 'My tasks — nothing awaiting you'
          }
          aria-label={`My tasks, ${myTasksCount} awaiting you`}
        >
          <Bell size={16} />
          {myTasksCount > 0 && (
            <span
              className="absolute -top-0.5 -right-0.5 min-w-[16px] h-[16px] px-1 rounded-full text-[10px] font-bold leading-[16px] text-center text-white"
              style={{ backgroundColor: 'var(--danger)' }}
            >
              {myTasksCount > 99 ? '99+' : myTasksCount}
            </span>
          )}
        </NavLink>

        <div className="w-px h-6 mx-1.5" style={{ backgroundColor: 'var(--border-color)' }} />

        <div className="relative" ref={userMenuRef}>
          <button
            onClick={() => setUserMenuOpen((prev) => !prev)}
            className="flex items-center gap-2 px-2 py-1.5 rounded-lg transition-colors hover:bg-[var(--bg-secondary)]"
          >
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

          {userMenuOpen && (
            <div
              className="absolute right-0 top-full mt-1 w-48 rounded-lg border shadow-lg py-1 z-50"
              style={{
                borderColor: 'var(--border-color)',
                backgroundColor: 'var(--bg-secondary)',
              }}
            >
              <div className="px-3 py-2 border-b" style={{ borderColor: 'var(--border-color)' }}>
                <p className="text-[13px] font-medium" style={{ color: 'var(--text-primary)' }}>
                  {user?.name}
                </p>
                <p className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
                  {user?.email}
                </p>
              </div>
              {(isLocalAuthEnabled() || isMsalEnabled()) && (
                <button
                  onClick={() => {
                    setUserMenuOpen(false);
                    if (isMsalEnabled()) {
                      void msalLogout();
                    } else {
                      logout();
                    }
                  }}
                  className="w-full flex items-center gap-2 px-3 py-2 text-[13px] transition-colors hover:bg-[var(--bg-primary)]"
                  style={{ color: 'var(--text-secondary)' }}
                >
                  <LogOut size={14} />
                  Sign out
                </button>
              )}
            </div>
          )}
        </div>
      </div>
    </header>
  );
}
