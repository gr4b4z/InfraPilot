import type { A2UIComponent } from '@/lib/types';

interface Props {
  component: A2UIComponent;
}

export function ReviewCard({ component }: Props) {
  return (
    <div
      className="rounded-xl border p-4 space-y-3"
      style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-secondary)' }}
    >
      <h3 className="font-semibold text-sm" style={{ color: 'var(--text-primary)' }}>
        Review summary
      </h3>
      <div className="space-y-2">
        {component.fields?.map((field, i) => (
          <div key={i} className="flex justify-between text-sm">
            <span style={{ color: 'var(--text-muted)' }}>{field.label}</span>
            <span className="font-medium" style={{ color: 'var(--text-primary)' }}>{field.value}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
