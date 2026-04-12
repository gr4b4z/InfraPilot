import { Link } from 'react-router-dom';
import type { CatalogItem } from '@/lib/types';
import {
  GitBranch,
  Play,
  Server,
  Globe,
  Shield,
  Activity,
  Package,
  ArrowRight,
} from 'lucide-react';

const iconMap: Record<string, React.ComponentType<{ size?: number; className?: string }>> = {
  'git-branch': GitBranch,
  'play': Play,
  'server': Server,
  'globe': Globe,
  'shield': Shield,
  'activity': Activity,
};

const categoryLabels: Record<string, string> = {
  'ci-cd': 'CI / CD',
  'infrastructure': 'Infrastructure',
  'access': 'Access Management',
};

interface Props {
  item: CatalogItem;
}

export function CatalogCard({ item }: Props) {
  const Icon = iconMap[item.icon || ''] || Package;

  return (
    <Link
      to={`/catalog/${item.slug}`}
      className="group card-hover flex flex-col p-5 rounded-xl border"
      style={{
        backgroundColor: 'var(--bg-primary)',
        borderColor: 'var(--border-color)',
      }}
    >
      <div className="flex items-start justify-between mb-4">
        <div
          className="flex items-center justify-center w-10 h-10 rounded-lg"
          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
        >
          <Icon size={20} />
        </div>
        <ArrowRight
          size={16}
          className="opacity-0 group-hover:opacity-100 transition-opacity mt-1"
          style={{ color: 'var(--accent)' }}
        />
      </div>

      <h3
        className="font-semibold text-[14px] mb-1 group-hover:text-[var(--accent)] transition-colors"
        style={{ color: 'var(--text-primary)' }}
      >
        {item.name}
      </h3>
      <p className="text-[12px] leading-relaxed flex-1" style={{ color: 'var(--text-muted)' }}>
        {item.description}
      </p>

      <div className="flex items-center justify-between mt-4 pt-3" style={{ borderTop: '1px solid var(--border-color)' }}>
        <span
          className="text-[11px] font-medium px-2 py-0.5 rounded-md"
          style={{ backgroundColor: 'var(--accent-muted)', color: 'var(--accent)' }}
        >
          {categoryLabels[item.category] || item.category}
        </span>
        {item.isActive && (
          <span className="flex items-center gap-1 text-[11px]" style={{ color: 'var(--success)' }}>
            <span className="w-1.5 h-1.5 rounded-full" style={{ backgroundColor: 'var(--success)' }} />
            Active
          </span>
        )}
      </div>
    </Link>
  );
}
