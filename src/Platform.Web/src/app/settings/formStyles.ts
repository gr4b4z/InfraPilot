/**
 * Shared input/label styling for the settings policy editors (promotions and rollbacks), so the two
 * policy forms stay visually identical.
 *
 * Kept out of `approverPickers.tsx` because that module exports components, and a module mixing
 * component and non-component exports breaks React Fast Refresh.
 */

export const inputClass =
  'px-2.5 py-1.5 rounded-lg border text-[13px] outline-none transition-colors focus:border-[var(--accent)]';

export const inputStyle = {
  borderColor: 'var(--border-color)',
  backgroundColor: 'var(--bg-primary)',
  color: 'var(--text-primary)',
};

export const labelClass = 'text-[11px] font-medium uppercase tracking-wider';

export const labelStyle = { color: 'var(--text-muted)' };
