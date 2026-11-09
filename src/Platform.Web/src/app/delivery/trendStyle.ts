import type { DeliveryTrendPoint } from '@/lib/api';

/** Brand-neutral hues for product overlays; the portfolio line always takes the accent. */
export const OVERLAY_COLORS = ['#0ea5e9', '#f59e0b', '#10b981', '#8b5cf6', '#ef4444', '#14b8a6', '#f97316', '#6366f1'];

/** Below this a bucket is drawn hollow: the value is real but rests on too little of its population. */
export const THIN_COVERAGE = 0.5;

export interface TrendSeries {
  name: string;
  points: DeliveryTrendPoint[];
  color: string;
}
