import { AlertTriangle } from 'lucide-react';

/**
 * What a directory picker shows when the search itself failed — `directoryUnavailable` on
 * `/promotions/users/search` and `/promotions/groups/search`, or the request erroring outright.
 * Without it the picker's empty state read "No directory matches", which looks like a typo or a
 * person who isn't there rather than an outage worth reporting. `detail` says what the user can do
 * instead in that picker.
 */
export function DirectoryUnavailableNotice({ detail }: { detail?: string }) {
  return (
    <div
      role="status"
      className="px-3 py-2 text-[12px] flex items-start gap-1.5"
      style={{ color: 'var(--warning)' }}
    >
      <AlertTriangle size={12} style={{ flexShrink: 0, marginTop: 2 }} />
      <span>
        Directory search is unavailable right now.
        {detail && <span style={{ color: 'var(--text-muted)' }}> {detail}</span>}
      </span>
    </div>
  );
}
