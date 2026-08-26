import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { CornerDownLeft } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { useAuthStore } from '@/stores/authStore';
import { useFeatureFlagsStore } from '@/stores/featureFlagsStore';
import { NAV_TARGETS } from './navTargets';

/**
 * The `:` navigation palette.
 *
 * `:` used to be a silent prefix — you had to already know that `d` followed it. Showing the menu the
 * moment the prefix is pressed means the accelerators are discoverable by pressing one key, while
 * still being a two-keystroke jump for anyone who has learned them.
 *
 * Deliberately *not* a filter box. The letters are the accelerators, so a text input would swallow
 * them and turn every jump into type-then-Enter. Arrow keys and Enter are there for browsing; the
 * letter is there for speed.
 */
export function CommandPalette({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const isAdmin = useAuthStore((s) => s.user?.isAdmin ?? false);
  const flags = useFeatureFlagsStore((s) => s.flags);
  const [highlighted, setHighlighted] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  // Same visibility rules as the sidebar, so the palette can't offer a page the nav doesn't have.
  const targets = useMemo(
    () =>
      NAV_TARGETS.filter((t) => {
        if (t.adminOnly && !isAdmin) return false;
        if (t.featureFlag && flags[t.featureFlag] === false) return false;
        return true;
      }),
    [isAdmin, flags],
  );

  useEffect(() => {
    const go = (to: string) => {
      onClose();
      navigate(to);
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;

      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        setHighlighted((current) => {
          const next = current + (event.key === 'ArrowDown' ? 1 : -1);
          const clamped = Math.max(0, Math.min(next, targets.length - 1));
          listRef.current
            ?.querySelectorAll<HTMLElement>('[data-palette-item]')[clamped]
            ?.scrollIntoView({ block: 'nearest' });
          return clamped;
        });
        return;
      }
      if (event.key === 'Enter') {
        event.preventDefault();
        const target = targets[highlighted];
        if (target) go(target.to);
        return;
      }
      // The accelerator itself. Single characters only, so Escape and Tab fall through to Dialog.
      if (event.key.length === 1) {
        const target = targets.find((t) => t.key === event.key.toLowerCase());
        if (target) {
          event.preventDefault();
          event.stopPropagation();
          go(target.to);
        }
      }
    };

    // Capture, so an accelerator is claimed before the app-level hotkey layer sees it.
    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, [targets, highlighted, navigate, onClose]);

  return (
    <Dialog onClose={onClose} ariaLabel="Go to" width={420}>
      <div
        className="px-3 py-2.5 border-b flex items-center justify-between"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <span className="text-[13px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Go to
        </span>
        <span className="text-[11px]" style={{ color: 'var(--text-muted)' }}>
          press a key · ↑↓ · Esc
        </span>
      </div>

      <div ref={listRef} role="listbox" aria-label="Destinations" className="max-h-[60vh] overflow-y-auto py-1">
        {targets.map((target, i) => {
          const Icon = target.icon;
          const active = highlighted === i;
          return (
            <button
              key={target.to}
              data-palette-item
              role="option"
              aria-selected={active}
              type="button"
              tabIndex={-1}
              onMouseEnter={() => setHighlighted(i)}
              onClick={() => { onClose(); navigate(target.to); }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-left transition-colors"
              style={{ backgroundColor: active ? 'var(--accent-muted)' : undefined }}
            >
              <Icon size={14} className="shrink-0" />
              <span className="flex-1 text-[13px]" style={{ color: 'var(--text-primary)' }}>
                {target.label}
              </span>
              <kbd
                className="px-1.5 py-0.5 rounded text-[10px] font-mono font-semibold"
                style={{
                  backgroundColor: active ? 'var(--accent)' : 'var(--bg-secondary)',
                  color: active ? '#fff' : 'var(--text-muted)',
                  border: `1px solid ${active ? 'var(--accent)' : 'var(--border-color)'}`,
                }}
              >
                {target.key}
              </kbd>
              {active && <CornerDownLeft size={11} style={{ color: 'var(--text-muted)' }} />}
            </button>
          );
        })}
      </div>
    </Dialog>
  );
}
