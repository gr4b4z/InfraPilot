import { useState, useEffect } from 'react';
import { CatalogGrid } from '@/components/catalog/CatalogGrid';
import { CategoryFilter } from '@/components/catalog/CategoryFilter';
import type { CatalogItem } from '@/lib/types';
import { api } from '@/lib/api';
import { LayoutGrid, Search, TrendingUp, Clock, CheckCircle, MessageSquare } from 'lucide-react';
import { Link } from 'react-router-dom';

const categories = ['ci-cd', 'infrastructure', 'access', 'data', 'general'];

export function CatalogPage() {
  const [items, setItems] = useState<CatalogItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedCategory, setSelectedCategory] = useState<string | null>(null);
  const [search, setSearch] = useState('');

  useEffect(() => {
    api.getCatalog()
      .then((data) => setItems(data.items || []))
      .catch(() => setItems([]))
      .finally(() => setLoading(false));
  }, []);

  const filteredItems = items.filter((item) => {
    if (selectedCategory && item.category !== selectedCategory) return false;
    if (search && !item.name.toLowerCase().includes(search.toLowerCase()) &&
        !item.description?.toLowerCase().includes(search.toLowerCase())) return false;
    return true;
  });

  return (
    <div className="space-y-6">
      {/* Page header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold tracking-tight" style={{ color: 'var(--text-primary)' }}>
            Service Catalog
          </h1>
          <p className="text-[13px] mt-1" style={{ color: 'var(--text-muted)' }}>
            Browse and request infrastructure services for your team
          </p>
        </div>
      </div>

      {/* Stats row */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        {[
          { label: 'Available Services', value: items.length, icon: LayoutGrid, color: 'var(--accent)' },
          { label: 'Categories', value: categories.length, icon: TrendingUp, color: 'var(--info)' },
          { label: 'Active', value: items.filter(i => i.isActive).length, icon: CheckCircle, color: 'var(--success)' },
          { label: 'Recently Added', value: 2, icon: Clock, color: 'var(--warning)' },
        ].map((stat) => (
          <div
            key={stat.label}
            className="flex items-center gap-3 p-3.5 rounded-xl border"
            style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
          >
            <div
              className="w-9 h-9 rounded-lg flex items-center justify-center shrink-0"
              style={{ backgroundColor: stat.color + '12', color: stat.color }}
            >
              <stat.icon size={16} />
            </div>
            <div>
              <p className="text-lg font-semibold leading-none" style={{ color: 'var(--text-primary)' }}>{stat.value}</p>
              <p className="text-[11px] mt-0.5" style={{ color: 'var(--text-muted)' }}>{stat.label}</p>
            </div>
          </div>
        ))}
      </div>

      {/* Filters */}
      <div
        className="flex flex-col sm:flex-row gap-3 items-start sm:items-center p-4 rounded-xl border"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      >
        <CategoryFilter categories={categories} selected={selectedCategory} onSelect={setSelectedCategory} />
        <div className="relative sm:ml-auto w-full sm:w-64">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2" style={{ color: 'var(--text-muted)' }} />
          <input
            type="text"
            placeholder="Search services..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="w-full pl-9 pr-3 py-2 text-[13px] rounded-lg border outline-none transition-colors focus:border-[var(--accent)]"
            style={{
              backgroundColor: 'var(--bg-secondary)',
              borderColor: 'var(--border-color)',
              color: 'var(--text-primary)',
            }}
          />
        </div>
      </div>

      {/* Grid */}
      {loading ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {[1, 2, 3].map((i) => (
            <div key={i} className="skeleton h-[180px]" />
          ))}
        </div>
      ) : (
        <CatalogGrid items={filteredItems} />
      )}

      {/* General request CTA */}
      <Link
        to="/catalog/general-request/request"
        className="block p-5 rounded-xl border-2 border-dashed text-center transition-colors hover:border-[var(--accent)]"
        style={{ borderColor: 'var(--border-color)', backgroundColor: 'var(--bg-primary)' }}
      >
        <MessageSquare size={24} className="mx-auto mb-2" style={{ color: 'var(--text-muted)' }} />
        <p className="text-sm font-medium" style={{ color: 'var(--text-primary)' }}>
          Can't find what you need?
        </p>
        <p className="text-xs mt-1" style={{ color: 'var(--text-muted)' }}>
          Submit a general request and our team will help
        </p>
      </Link>
    </div>
  );
}
