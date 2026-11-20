import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { useEntityRefresh } from '@/hooks/useEntityEvents';
import type { MigrationFlag } from '@/lib/types';

/**
 * The open migrations of a product (optionally one service), kept current as migrations are
 * reported. A failure to load is not a fact about any deployment, so it just means no flares.
 */
export function useMigrationFlags(product: string | undefined, service?: string): MigrationFlag[] {
  const [flags, setFlags] = useState<MigrationFlag[]>([]);
  const tick = useEntityRefresh(['migration'], {
    filter: (evt) => !evt.product || evt.product === product,
  });
  useEffect(() => {
    if (!product) return;
    let cancelled = false;
    api
      .getMigrationFlags({ product, service })
      .then((f) => { if (!cancelled) setFlags(f); })
      .catch(() => { if (!cancelled) setFlags([]); });
    return () => { cancelled = true; };
  }, [product, service, tick]);
  return flags;
}
