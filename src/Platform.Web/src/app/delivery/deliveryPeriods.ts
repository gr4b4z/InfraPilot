/**
 * Reporting windows. Calendar presets rather than rolling ones wherever a report is likely to be
 * re-opened: "last month" holds still, "last 30 days" shifts every day and two looks at the same
 * report disagree. The trend grain follows the span — weeks up to a quarter, months beyond.
 */
export const DELIVERY_PERIODS = [
  { key: '30', label: '30 days' },
  { key: 'this-month', label: 'This month' },
  { key: 'last-month', label: 'Last month' },
  { key: 'this-quarter', label: 'This quarter' },
  { key: 'last-quarter', label: 'Last quarter' },
  { key: '6-months', label: '6 months' },
  { key: '12-months', label: '12 months' },
] as const;

export type DeliveryPeriodKey = (typeof DELIVERY_PERIODS)[number]['key'];

export interface DeliveryRange {
  from: string;
  to?: string;
  days: number;
  bucket: 'week' | 'month';
}

export function computeDeliveryRange(key: string, now = new Date()): DeliveryRange {
  const startOfMonth = (y: number, m: number) => new Date(y, m, 1);
  const quarterStartMonth = Math.floor(now.getMonth() / 3) * 3;
  let from: Date;
  let to: Date | undefined;
  switch (key) {
    case 'this-month':
      from = startOfMonth(now.getFullYear(), now.getMonth());
      break;
    case 'last-month':
      from = startOfMonth(now.getFullYear(), now.getMonth() - 1);
      to = startOfMonth(now.getFullYear(), now.getMonth());
      break;
    case 'this-quarter':
      from = startOfMonth(now.getFullYear(), quarterStartMonth);
      break;
    case 'last-quarter':
      from = startOfMonth(now.getFullYear(), quarterStartMonth - 3);
      to = startOfMonth(now.getFullYear(), quarterStartMonth);
      break;
    case '6-months':
      from = startOfMonth(now.getFullYear(), now.getMonth() - 5);
      break;
    case '12-months':
      from = startOfMonth(now.getFullYear(), now.getMonth() - 11);
      break;
    default:
      from = new Date(now.getTime() - 30 * 86_400_000);
  }
  const end = to ?? now;
  const days = Math.max(1, (end.getTime() - from.getTime()) / 86_400_000);
  return {
    from: from.toISOString(),
    to: to?.toISOString(),
    days,
    bucket: days > 100 ? 'month' : 'week',
  };
}
