interface Props {
  categories: string[];
  selected: string | null;
  onSelect: (category: string | null) => void;
}

const categoryLabels: Record<string, string> = {
  'ci-cd': 'CI / CD',
  'infrastructure': 'Infrastructure',
  'access': 'Access',
};

export function CategoryFilter({ categories, selected, onSelect }: Props) {
  return (
    <div className="flex gap-1.5 flex-wrap">
      <button
        onClick={() => onSelect(null)}
        className="px-3 py-1.5 text-[12px] font-medium rounded-lg border transition-all duration-150"
        style={{
          backgroundColor: selected === null ? 'var(--accent)' : 'transparent',
          borderColor: selected === null ? 'var(--accent)' : 'var(--border-color)',
          color: selected === null ? 'white' : 'var(--text-secondary)',
        }}
      >
        All services
      </button>
      {categories.map((cat) => (
        <button
          key={cat}
          onClick={() => onSelect(cat)}
          className="px-3 py-1.5 text-[12px] font-medium rounded-lg border transition-all duration-150"
          style={{
            backgroundColor: selected === cat ? 'var(--accent)' : 'transparent',
            borderColor: selected === cat ? 'var(--accent)' : 'var(--border-color)',
            color: selected === cat ? 'white' : 'var(--text-secondary)',
          }}
        >
          {categoryLabels[cat] || cat}
        </button>
      ))}
    </div>
  );
}
