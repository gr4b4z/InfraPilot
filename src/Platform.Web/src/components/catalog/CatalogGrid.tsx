import type { CatalogItem } from '@/lib/types';
import { CatalogCard } from './CatalogCard';

interface Props {
  items: CatalogItem[];
}

export function CatalogGrid({ items }: Props) {
  if (items.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16" style={{ color: 'var(--text-muted)' }}>
        <p className="text-lg">No services found</p>
        <p className="text-sm mt-1">Try adjusting your search or filters</p>
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      {items.map((item) => (
        <CatalogCard key={item.id} item={item} />
      ))}
    </div>
  );
}
