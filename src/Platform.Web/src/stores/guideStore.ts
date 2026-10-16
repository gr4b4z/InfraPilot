import { create } from 'zustand';

/** One step of a running walkthrough, as sent by the agent's start_guide tool. */
export interface GuideStep {
  text: string;
  /** `data-guide-anchor` value to spotlight. Absent when the step has no single control. */
  anchor?: string;
  /** Route to move to before this step, when it differs from the previous one. */
  route?: string;
  note?: string;
}

export interface GuidePlan {
  id: string;
  title: string;
  summary: string;
  route: string;
  steps: GuideStep[];
}

interface GuideState {
  plan: GuidePlan | null;
  /** Index into plan.steps. Meaningless while plan is null. */
  index: number;
  /**
   * Set when the current step names an anchor that is not in the DOM — usually because the control
   * is conditional on a role or on state the user has not reached yet. The overlay explains this
   * rather than silently highlighting nothing.
   */
  anchorMissing: boolean;

  start: (plan: GuidePlan) => void;
  next: () => void;
  back: () => void;
  goTo: (index: number) => void;
  setAnchorMissing: (missing: boolean) => void;
  stop: () => void;
}

export const useGuideStore = create<GuideState>((set, get) => ({
  plan: null,
  index: 0,
  anchorMissing: false,

  start: (plan) => set({ plan, index: 0, anchorMissing: false }),

  next: () => {
    const { plan, index } = get();
    if (!plan) return;
    // Past the last step the walkthrough is done, not stuck on the end.
    if (index >= plan.steps.length - 1) {
      set({ plan: null, index: 0, anchorMissing: false });
      return;
    }
    set({ index: index + 1, anchorMissing: false });
  },

  back: () => {
    const { index } = get();
    set({ index: Math.max(0, index - 1), anchorMissing: false });
  },

  goTo: (index) => {
    const { plan } = get();
    if (!plan) return;
    set({ index: Math.min(Math.max(0, index), plan.steps.length - 1), anchorMissing: false });
  },

  setAnchorMissing: (anchorMissing) => set({ anchorMissing }),

  stop: () => set({ plan: null, index: 0, anchorMissing: false }),
}));
