import type { CSSProperties, ReactNode } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { useEnvColor } from './useEnvColor';

/**
 * The single place environments get rendered with their colour. Anything that names an
 * environment — a promotion's source/target, a rollback's target, a deploy activity row,
 * a table column header — should go through one of these so the colour, the label lookup,
 * and the theme-adaptive mixing stay consistent. Components that need the raw colour tokens
 * instead of a badge use the hooks in `./useEnvColor`.
 */

/** Colour + display label for an environment key. */
function useEnv(env: string) {
  const displayName = useSettingsStore((s) => s.getDisplayName(env));
  return { ...useEnvColor(env), displayName };
}

/**
 * Solid colour dot. For places where a full pill would be too heavy — table headers,
 * dropdown rows, dense inline lists — but the environment still needs to be identifiable.
 */
export function EnvDot({ env, size = 8, className, style }: {
  env: string;
  size?: number;
  className?: string;
  style?: CSSProperties;
}) {
  const { solid } = useEnvColor(env);
  return (
    <span
      aria-hidden
      className={className}
      style={{
        display: 'inline-block',
        width: size,
        height: size,
        borderRadius: '50%',
        backgroundColor: solid,
        flexShrink: 0,
        ...style,
      }}
    />
  );
}

/**
 * Environment pill: tinted background, colour-matched border and label.
 *
 * `suffix` renders in muted text after the label — used for the version an environment is
 * on ("Production (2.4.1)") so the pill replaces the plain text that was there before rather
 * than sitting next to it.
 */
export function EnvBadge({
  env,
  suffix,
  title,
  size = 'sm',
  showDot = false,
  className = '',
  style,
}: {
  env: string;
  suffix?: ReactNode;
  title?: string;
  /** `xs` for dense chip rows, `sm` (default) for card headers and filters. */
  size?: 'xs' | 'sm';
  /** Add a solid dot inside the pill — useful when the pill sits among other coloured chips. */
  showDot?: boolean;
  className?: string;
  style?: CSSProperties;
}) {
  const { fg, bg, border, displayName } = useEnv(env);
  const dims =
    size === 'xs'
      ? { fontSize: 10, padding: '1px 6px', gap: 4 }
      : { fontSize: 11, padding: '2px 8px', gap: 5 };

  return (
    <span
      className={`inline-flex items-center font-semibold rounded-full ${className}`}
      title={title ?? displayName}
      style={{
        color: fg,
        backgroundColor: bg,
        border: `1px solid ${border}`,
        letterSpacing: '0.02em',
        lineHeight: '16px',
        whiteSpace: 'nowrap',
        ...dims,
        ...style,
      }}
    >
      {showDot && <EnvDot env={env} size={size === 'xs' ? 5 : 6} />}
      {displayName}
      {suffix !== undefined && suffix !== null && (
        <span style={{ fontWeight: 500, opacity: 0.75 }}>{suffix}</span>
      )}
    </span>
  );
}

/**
 * Just the label, in the environment's colour. For headings and table headers where a pill
 * would fight with the surrounding layout but the colour still helps you find the column.
 */
export function EnvLabel({ env, className, style }: {
  env: string;
  className?: string;
  style?: CSSProperties;
}) {
  const { fg, displayName } = useEnv(env);
  return (
    <span className={className} style={{ color: fg, ...style }}>
      {displayName}
    </span>
  );
}
