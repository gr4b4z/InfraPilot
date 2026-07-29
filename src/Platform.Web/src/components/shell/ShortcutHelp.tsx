import { Dialog } from '@/components/ui/Dialog';
import { shortcutGroups } from './shortcuts';

/** The `?` overlay. Reads {@link shortcutGroups}, so it can't drift from the bindings by omission. */
export function ShortcutHelp({ open, onClose }: { open: boolean; onClose: () => void }) {
  if (!open) return null;

  return (
    <Dialog onClose={onClose} ariaLabel="Keyboard shortcuts" width={640}>
      <div
        className="flex items-center justify-between px-4 py-3 border-b"
        style={{ borderColor: 'var(--border-color)' }}
      >
        <h2 className="text-[14px] font-semibold" style={{ color: 'var(--text-primary)' }}>
          Keyboard shortcuts
        </h2>
        <button
          type="button"
          onClick={onClose}
          className="text-[12px] font-medium transition-opacity hover:opacity-80"
          style={{ color: 'var(--text-muted)' }}
        >
          Esc to close
        </button>
      </div>

      <div className="max-h-[70vh] overflow-y-auto p-4 grid grid-cols-1 sm:grid-cols-2 gap-x-8 gap-y-5">
        {shortcutGroups().map((group) => (
          <section key={group.title}>
            <h3
              className="text-[10px] font-semibold uppercase tracking-wider mb-2"
              style={{ color: 'var(--text-muted)' }}
            >
              {group.title}
            </h3>
            <dl className="space-y-1.5">
              {group.items.map((item) => (
                <div key={item.description} className="flex items-baseline justify-between gap-3">
                  <dd className="text-[12px] min-w-0" style={{ color: 'var(--text-secondary)' }}>
                    {item.description}
                  </dd>
                  <dt className="flex items-center gap-1 shrink-0">
                    {item.keys.map((key, i) => (
                      <kbd
                        key={`${key}-${i}`}
                        className="px-1.5 py-0.5 rounded text-[10px] font-mono font-medium"
                        style={{
                          backgroundColor: 'var(--bg-secondary)',
                          color: 'var(--text-primary)',
                          border: '1px solid var(--border-color)',
                        }}
                      >
                        {key}
                      </kbd>
                    ))}
                  </dt>
                </div>
              ))}
            </dl>
          </section>
        ))}
      </div>

      <p
        className="px-4 py-2.5 border-t text-[11px]"
        style={{ borderColor: 'var(--border-color)', color: 'var(--text-muted)' }}
      >
        Single-key shortcuts are suspended while you're typing in a field, so they never fight text
        entry.
      </p>
    </Dialog>
  );
}
