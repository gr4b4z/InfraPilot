import { create } from 'zustand';

/**
 * Hidden colour themes — an easter egg, not a setting.
 *
 * The portal's real theme control is the light/dark/system switch in the topbar. These four are
 * reachable only through the `t` keyboard chords in {@link KeyboardLayer} and are deliberately left
 * out of the `?` shortcut help. Each one is a class on `<html>` whose CSS block in `index.css`
 * redefines every semantic token (`--bg-*`, `--text-*`, `--accent*`, status colours), so the whole
 * app recolours without any component knowing the theme exists.
 *
 * A hidden theme sits *on top of* the light/dark choice rather than replacing it: the topbar's
 * switch keeps working underneath, and clearing the hidden theme returns to whatever it was set to.
 */
export type HiddenTheme = 'matrix' | 'punk' | 'cyberpunk' | 'forest';

export interface HiddenThemeInfo {
  id: HiddenTheme;
  /** Shown in the toast that confirms the switch. */
  name: string;
  /** One-line flavour text for the toast. */
  tagline: string;
  /** Class placed on `<html>`; the token block in `index.css` is keyed on it. */
  className: string;
  /**
   * Whether native controls (scrollbars, form widgets, the date picker) should render dark. Set on
   * the root as `color-scheme` because the topbar's light/dark logic otherwise owns that property.
   */
  colorScheme: 'light' | 'dark';
}

export const HIDDEN_THEMES: readonly HiddenThemeInfo[] = [
  { id: 'matrix', name: 'Matrix', tagline: 'Follow the white rabbit.', className: 'theme-matrix', colorScheme: 'dark' },
  { id: 'punk', name: 'Punk', tagline: 'No future. Only deploys.', className: 'theme-punk', colorScheme: 'light' },
  { id: 'cyberpunk', name: 'Cyberpunk', tagline: 'Wake up, samurai. We have a release to ship.', className: 'theme-cyberpunk', colorScheme: 'dark' },
  { id: 'forest', name: 'Forest Fairy', tagline: 'Something glows between the trees.', className: 'theme-forest', colorScheme: 'dark' },
];

/** Every hidden-theme class, so the applier can clear them all before adding one. */
export const HIDDEN_THEME_CLASSES = HIDDEN_THEMES.map((t) => t.className);

export function hiddenThemeInfo(id: HiddenTheme): HiddenThemeInfo {
  return HIDDEN_THEMES.find((t) => t.id === id)!;
}

const STORAGE_KEY = 'hidden-theme';
const SOUND_STORAGE_KEY = 'hidden-theme-sound';

function readStored(): HiddenTheme | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return HIDDEN_THEMES.some((t) => t.id === raw) ? (raw as HiddenTheme) : null;
  } catch {
    return null;
  }
}

function writeStored(theme: HiddenTheme | null): void {
  try {
    if (theme) window.localStorage.setItem(STORAGE_KEY, theme);
    else window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage can be unavailable (private mode, blocked). The theme still applies for this session.
  }
}

function readStoredSound(): boolean {
  try {
    return window.localStorage.getItem(SOUND_STORAGE_KEY) === 'on';
  } catch {
    return false;
  }
}

function writeStoredSound(on: boolean): void {
  try {
    if (on) window.localStorage.setItem(SOUND_STORAGE_KEY, 'on');
    else window.localStorage.removeItem(SOUND_STORAGE_KEY);
  } catch {
    // See writeStored.
  }
}

interface HiddenThemeState {
  /** Active hidden theme, or null for the ordinary light/dark portal. */
  theme: HiddenTheme | null;
  /**
   * Ambient music for the active theme (see `lib/hiddenThemeAudio`). Off by default — a sudden
   * soundtrack in an open-plan office is a worse surprise than a colour change — and remembered
   * once switched on, so it comes back with the theme.
   */
  sound: boolean;
  /**
   * Bumped on every change, including re-selecting the current theme, so the confirmation toast
   * re-shows even when the theme itself didn't move.
   */
  changedAt: number;
  /** Which switch `changedAt` refers to, so the toast can word itself accordingly. */
  lastChange: 'theme' | 'sound';

  setTheme: (theme: HiddenTheme | null) => void;
  /** none → matrix → punk → cyberpunk → forest → none. */
  cycle: () => void;
  toggleSound: () => void;
}

export const useHiddenThemeStore = create<HiddenThemeState>()((set, get) => ({
  theme: typeof window === 'undefined' ? null : readStored(),
  sound: typeof window === 'undefined' ? false : readStoredSound(),
  changedAt: 0,
  lastChange: 'theme',

  setTheme: (theme) => {
    writeStored(theme);
    set({ theme, changedAt: Date.now(), lastChange: 'theme' });
  },

  toggleSound: () => {
    const sound = !get().sound;
    writeStoredSound(sound);
    set({ sound, changedAt: Date.now(), lastChange: 'sound' });
  },

  cycle: () => {
    const current = get().theme;
    const index = current ? HIDDEN_THEMES.findIndex((t) => t.id === current) : -1;
    const next = index + 1 < HIDDEN_THEMES.length ? HIDDEN_THEMES[index + 1].id : null;
    get().setTheme(next);
  },
}));
