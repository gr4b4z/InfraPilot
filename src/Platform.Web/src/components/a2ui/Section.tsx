import type { ReactNode } from 'react';

interface Props {
  title: string;
  children: ReactNode;
}

export function Section({ title, children }: Props) {
  return (
    <div
      className="rounded-lg border p-4"
      style={{
        backgroundColor: 'var(--bg-secondary)',
        borderColor: 'var(--border-color)',
      }}
    >
      <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text-primary)' }}>
        {title}
      </h3>
      <div className="space-y-4">{children}</div>
    </div>
  );
}
