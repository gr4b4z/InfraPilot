import { useEffect, useRef } from 'react';
import { hasCommandModifier, isTypingTarget } from '@/lib/keys';

/**
 * How long a chord prefix stays armed. Long enough to type "g" then "d" without hurrying, short
 * enough that a stray "g" doesn't silently swallow the next keystroke a minute later.
 */
const CHORD_TIMEOUT_MS = 1200;

/**
 * A keyboard binding table.
 *
 * Keys are either a single key (`'/'`, `'?'`, `'a'`, `'A'`) or a two-step chord written with a space
 * (`'g d'`). Matching is case-sensitive, which is what makes `a` (assign) and `A` (approve) distinct
 * bindings rather than the same one.
 */
export type HotkeyMap = Record<string, (event: KeyboardEvent) => void>;

export interface UseHotkeysOptions {
  /** Set false to unbind without unmounting — e.g. while a modal owns the keyboard. */
  enabled?: boolean;
  /**
   * Allow these bindings to fire even when the user is typing in an input. Only for keys that can't
   * be part of normal text entry (`Escape`, `ArrowDown`), never for letters.
   */
  allowWhileTyping?: string[];
}

/**
 * Binds a document-level keyboard map for as long as the component is mounted.
 *
 * Single-key shortcuts are the point of this hook, so the guards matter more than the dispatch:
 * bindings are skipped while the user is typing, and skipped when Ctrl/Cmd/Alt is held so we never
 * shadow a browser or OS shortcut. Handlers that match get `preventDefault()` — `/` would otherwise
 * open Firefox's quick-find, and `?` would type a character into whatever gains focus next.
 *
 * Chords are matched with a short-lived prefix: pressing `g` arms it and swallows the keystroke,
 * and the next key either completes a binding or clears the prefix. Because the prefix is per-hook,
 * two components can both bind `g …` without colliding on the prefix itself.
 */
export function useHotkeys(map: HotkeyMap, options: UseHotkeysOptions = {}): void {
  const { enabled = true, allowWhileTyping } = options;

  // Handlers are re-created on most renders; keeping them in a ref means the listener is attached
  // once rather than being torn down and re-added on every parent render. Written in an effect
  // rather than during render — a ref write during render is not safe under concurrent rendering,
  // and the listener only ever reads these after mount anyway.
  const mapRef = useRef(map);
  const allowRef = useRef(allowWhileTyping);
  useEffect(() => {
    mapRef.current = map;
    allowRef.current = allowWhileTyping;
  });

  useEffect(() => {
    if (!enabled) return;

    let prefix: string | null = null;
    let prefixTimer: number | undefined;

    const clearPrefix = () => {
      prefix = null;
      if (prefixTimer !== undefined) window.clearTimeout(prefixTimer);
      prefixTimer = undefined;
    };

    const onKeyDown = (event: KeyboardEvent) => {
      const bindings = mapRef.current;
      const typing = isTypingTarget(event.target);
      const allowed = allowRef.current;

      if (typing && !(allowed?.includes(event.key))) {
        clearPrefix();
        return;
      }
      // Ctrl/Cmd/Alt combinations belong to the browser; Shift does not (it types `A`).
      if (hasCommandModifier(event)) {
        clearPrefix();
        return;
      }

      // Complete an armed chord first, so `g` followed by `d` can't also be read as a bare `d`.
      if (prefix) {
        const chord = `${prefix} ${event.key}`;
        clearPrefix();
        const chordHandler = bindings[chord];
        if (chordHandler) {
          event.preventDefault();
          chordHandler(event);
        }
        return;
      }

      // Arm a prefix when some binding starts with this key and there is no bare binding for it.
      const isPrefix = Object.keys(bindings).some((k) => k.startsWith(`${event.key} `));
      if (isPrefix && !bindings[event.key]) {
        event.preventDefault();
        prefix = event.key;
        prefixTimer = window.setTimeout(clearPrefix, CHORD_TIMEOUT_MS);
        return;
      }

      const handler = bindings[event.key];
      if (handler) {
        event.preventDefault();
        handler(event);
      }
    };

    document.addEventListener('keydown', onKeyDown);
    return () => {
      clearPrefix();
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [enabled]);
}
