/**
 * Environment colours.
 *
 * Environments are the main axis people scan by — "is this promotion going to prod?" — but
 * until now every environment rendered in the same neutral/accent chip, so telling them apart
 * meant reading. Each environment now carries an admin-chosen colour (Settings → Environments)
 * that tints every item targeting it.
 *
 * Two rules keep this predictable:
 *  1. An environment with no explicit colour still gets one, derived deterministically from its
 *     key (`autoEnvColor`). A fresh install is colour-coded without anyone configuring anything,
 *     and the same key always lands on the same swatch across users and reloads.
 *  2. The stored colour is a single hex value, not a light/dark pair. Rendering mixes it toward
 *     the current theme's text/background tokens (`envColorStyles`), so one swatch stays legible
 *     in both themes without the admin picking twice.
 */

/**
 * Swatches offered in the settings picker. Every one of these clears 4.5:1 against both the
 * tinted chip and the page background, in light *and* dark, once run through
 * `envColorStyles` — that constraint is why yellow is `#a16207` rather than a brighter
 * `#ca8a04`, which lands at 4.35:1 on white.
 */
export const ENV_COLOR_PRESETS: { value: string; name: string }[] = [
  { value: '#dc2626', name: 'Red' },
  { value: '#ea580c', name: 'Orange' },
  { value: '#d97706', name: 'Amber' },
  { value: '#a16207', name: 'Yellow' },
  { value: '#16a34a', name: 'Green' },
  { value: '#059669', name: 'Emerald' },
  { value: '#0d9488', name: 'Teal' },
  { value: '#0891b2', name: 'Cyan' },
  { value: '#2563eb', name: 'Blue' },
  { value: '#4f46e5', name: 'Indigo' },
  { value: '#7c3aed', name: 'Violet' },
  { value: '#c026d3', name: 'Fuchsia' },
  { value: '#db2777', name: 'Pink' },
  { value: '#64748b', name: 'Slate' },
];

/** Palette used when an environment has no explicit colour. Same hues as the presets. */
const AUTO_PALETTE = ENV_COLOR_PRESETS.map((p) => p.value);

/** Normalise user input to `#rrggbb`, or null when it isn't a hex colour. Mirrors the server. */
export function normalizeHexColor(value: string | null | undefined): string | null {
  const hex = (value ?? '').trim().replace(/^#/, '');
  if (hex.length !== 3 && hex.length !== 6) return null;
  if (!/^[0-9a-fA-F]+$/.test(hex)) return null;
  const full = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex;
  return '#' + full.toLowerCase();
}

/**
 * Stable colour for an environment key. FNV-1a so the mapping is deterministic across
 * browsers and reloads — an unconfigured "uat" is the same colour for everyone.
 */
export function autoEnvColor(key: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < key.length; i++) {
    hash ^= key.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return AUTO_PALETTE[hash % AUTO_PALETTE.length];
}

/**
 * Theme-adaptive styles for a colour.
 *
 * Foreground mixes toward `--text-primary` (near-black in light, near-white in dark), so a
 * single stored hex reads correctly in both themes instead of vanishing into one of them.
 * Background/border are low-opacity mixes over whatever the surface happens to be.
 */
export function envColorStyles(color: string): {
  /** Legible text colour on a tinted or plain surface. */
  fg: string;
  /** Tinted chip background. */
  bg: string;
  /** Chip border / rule. */
  border: string;
  /** The raw colour, for solid marks like dots and accent bars. */
  solid: string;
} {
  return {
    fg: `color-mix(in srgb, ${color} 70%, var(--text-primary))`,
    bg: `color-mix(in srgb, ${color} 14%, transparent)`,
    border: `color-mix(in srgb, ${color} 38%, transparent)`,
    solid: color,
  };
}
