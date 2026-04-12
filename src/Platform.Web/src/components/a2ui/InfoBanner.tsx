import { Info, AlertTriangle, XCircle } from 'lucide-react';
import type { A2UIComponent } from '@/lib/types';

interface Props {
  component: A2UIComponent;
}

const icons = {
  info: <Info size={16} />,
  warning: <AlertTriangle size={16} />,
  error: <XCircle size={16} />,
};

const styles = {
  info: { bg: 'var(--color-swo-light-blue)', border: 'var(--color-swo-blue)' },
  warning: { bg: 'var(--color-swo-yellow)', border: '#B8A800' },
  error: { bg: '#FEE2E2', border: '#EF4444' },
};

export function InfoBanner({ component }: Props) {
  const severity = component.severity || 'info';
  const style = styles[severity];

  return (
    <div
      className="flex items-start gap-2 p-3 rounded-lg border-l-4 text-sm"
      style={{
        backgroundColor: `${style.bg}20`,
        borderLeftColor: style.border,
        color: 'var(--text-primary)',
      }}
    >
      <span className="mt-0.5 shrink-0">{icons[severity]}</span>
      <span>{component.content}</span>
    </div>
  );
}
