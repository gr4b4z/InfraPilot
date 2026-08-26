import { useEffect } from 'react';
import { create } from 'zustand';
import { api } from '@/lib/api';
import type { PendingTicket, PromotionCandidate } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useFeatureFlagsStore, FeatureFlag } from '@/stores/featureFlagsStore';

/**
 * "Awaiting my action" rollup — the single source for the sidebar counters, the topbar bell
 * badge, and the My Tasks page. One fetch pair feeds all three so the numbers can't disagree
 * with each other or with the page they link to.
 *
 * Three sources, all scoped to what the current user can act on:
 *  - Promotions the user can approve right now (Pending × canApprove).
 *  - Work items the user is answerable for and hasn't signed off — the queue's `assignee=me` slice
 *    narrowed to the roles the promotion policy requires (`roleRequirement=assigned`).
 *  - Work items with nobody in a policy-required role (`roleRequirement=missing`). Not scoped to the
 *    user: the work they need is an assignment, and whoever can see the queue is who does that.
 *
 * Deliberately *not* "everything I'm authorised to touch": the work-item queue's unfiltered
 * pending list is the whole approver-group backlog, which for a group of any size is a number
 * nobody can act on. "Assigned to me" is the actionable subset, and it's what the badge promises.
 */
interface MyTasksState {
  /** Pending promotions the current user can approve. */
  promotions: PromotionCandidate[];
  /** Pending work items where the current user holds a policy-required participant role. */
  workItems: PendingTicket[];
  /**
   * Pending work items missing somebody in a role their promotion policy requires. Empty for users
   * without the QA/Admin role — the queue itself is empty for them.
   */
  unassignedWorkItems: PendingTicket[];
  loading: boolean;
  /** True once a fetch has settled — lets consumers hide a badge until the count is real. */
  loaded: boolean;
  /** Set when one of the two fetches failed; the other side's data is still valid. */
  error: string | null;
  refresh: () => Promise<void>;
}

/**
 * Shared in-flight fetch. Sidebar, topbar and the My Tasks page all mount at once and the
 * poller ticks on top of that, so without this the same two requests would go out several
 * times over. Callers get the promise of the fetch already running.
 */
let inFlight: Promise<void> | null = null;

const EMPTY_QUEUE = { tickets: [] as PendingTicket[], assignees: [] };

export const useMyTasksStore = create<MyTasksState>((set) => ({
  promotions: [],
  workItems: [],
  unassignedWorkItems: [],
  loading: false,
  loaded: false,
  error: null,
  refresh: () => {
    if (inFlight) return inFlight;
    inFlight = (async () => {
      const email = useAuthStore.getState().user?.email ?? '';
      // All three sources live behind the Promotions flag. When it's off there's nothing to count,
      // and hitting the endpoints would just be 404s per poll.
      if (useFeatureFlagsStore.getState().flags[FeatureFlag.Promotions] === false) {
        set({
          promotions: [],
          workItems: [],
          unassignedWorkItems: [],
          loading: false,
          loaded: true,
          error: null,
        });
        return;
      }
      set({ loading: true, error: null });
      // Settled, not all-or-nothing: a failure on one side shouldn't blank out the other side's
      // count, which would read as "you're all caught up" when it isn't.
      const [promotionsResult, workItemsResult, unassignedResult] = await Promise.allSettled([
        api.listPromotions({ status: 'Pending' }),
        // Without an email there is no "me" to narrow to, and an empty `assignee` would widen
        // the query to the entire approver-group backlog. Skip rather than over-count.
        email
          ? api.getMyPendingWorkItems({ assignee: email, roleRequirement: 'assigned' })
          : Promise.resolve(EMPTY_QUEUE),
        // Not narrowed by person — an item nobody has been put on has no "me" to match. The server
        // already limits the queue to users who may manage work items.
        api.getMyPendingWorkItems({ roleRequirement: 'missing' }),
      ]);

      const failures: string[] = [];
      const promotions =
        promotionsResult.status === 'fulfilled'
          ? (promotionsResult.value.candidates ?? []).filter(
              (c) => c.status === 'Pending' && c.canApprove,
            )
          : (failures.push('promotions'), []);
      const workItems =
        workItemsResult.status === 'fulfilled'
          ? (workItemsResult.value.tickets ?? [])
          : (failures.push('work items'), []);
      const unassignedWorkItems =
        unassignedResult.status === 'fulfilled'
          ? (unassignedResult.value.tickets ?? [])
          : (failures.push('unassigned work items'), []);

      set({
        promotions,
        workItems,
        unassignedWorkItems,
        loading: false,
        loaded: true,
        error: failures.length > 0 ? `Couldn't load ${failures.join(' and ')}.` : null,
      });
    })().finally(() => {
      inFlight = null;
    });
    return inFlight;
  },
}));

/**
 * Total items awaiting the current user — what the bell badge shows. Unassigned work items count:
 * putting somebody on a required role is an action, and one nobody else is going to be nudged about.
 */
export function useMyTasksCount(): number {
  return useMyTasksStore(
    (s) => s.promotions.length + s.workItems.length + s.unassignedWorkItems.length,
  );
}

const POLL_INTERVAL_MS = 60_000;

/**
 * Keeps the rollup fresh for the whole shell. Mounted once, in the Layout. Re-runs when the
 * signed-in identity resolves (the MSAL path sets the user an effect after the shell's first
 * paint, so the initial fetch would otherwise have no "me" to narrow by) and refetches while
 * the tab is visible.
 */
export function useMyTasksPolling(): void {
  const email = useAuthStore((s) => s.user?.email ?? '');
  const promotionsEnabled = useFeatureFlagsStore((s) => s.flags[FeatureFlag.Promotions] !== false);

  useEffect(() => {
    // Chained, not deduped: on the identity-arrival run there is usually a fetch already in
    // flight from the mount run, and that one was issued with no "me" to narrow work items by.
    // Joining it would leave the work-item count at zero until the next tick.
    refreshMyTasks();
    const refresh = () => void useMyTasksStore.getState().refresh();
    const id = window.setInterval(() => {
      // Skip ticks for a backgrounded tab — the visibility listener below catches up on return.
      if (document.visibilityState === 'visible') refresh();
    }, POLL_INTERVAL_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible') refresh();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearInterval(id);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [email, promotionsEnabled]);
}

/**
 * Fire-and-forget refresh for pages that just changed something the counters depend on
 * (approving a promotion, assigning a work item to someone). Chains behind a fetch already in
 * flight rather than joining it — that one was issued before the write and would come back with
 * the pre-change counts.
 */
export function refreshMyTasks(): void {
  if (inFlight) {
    void inFlight.then(() => useMyTasksStore.getState().refresh());
    return;
  }
  void useMyTasksStore.getState().refresh();
}
