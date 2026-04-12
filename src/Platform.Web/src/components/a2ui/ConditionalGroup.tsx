import type { ReactNode } from 'react';

interface Props {
  visibleWhen: { field: string; equals: unknown };
  values: Record<string, unknown>;
  children: ReactNode;
}

export function ConditionalGroup({ visibleWhen, values, children }: Props) {
  if (values[visibleWhen.field] !== visibleWhen.equals) {
    return null;
  }

  return <>{children}</>;
}
