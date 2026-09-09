import type { PromotionCandidate } from '@/lib/api';

/**
 * "What does pressing Approve actually do?" — the one thing an approver most needs to know and the
 * page could not previously tell them.
 *
 * Two answers, per edge, from the promotion policy's `deploysOnApproval` (Settings → Promotion
 * policies → After approval):
 *
 * - **It deploys** (the default, and the mpt-release path): InfraPortal's approval is the last gate.
 *   The release automation acts on it and rolls the version out, so Approve is Deploy.
 * - **It does not** (the SDP path — the marketplace repo): the approval starts a deployment run that
 *   stops at an approval of its own, outside InfraPortal, before the target environment changes.
 *
 * Both are worth saying, but only the first is a warning: somebody who thinks they are queueing a
 * release and is in fact shipping to production is the mistake this exists to prevent. The second is
 * informational — it stops the opposite mistake, waiting for a deploy that nobody has released yet.
 *
 * The wording is about the **gate**, not about this one click. A policy may want two signatures, so
 * "your approval deploys this" would be a lie on the first of them; "once the gate is satisfied" is
 * true either way and still says what the approver is signing up for.
 *
 * Single place the wording lives, so the notice on the promotion page, the confirmation that asks,
 * and the line above bulk approve cannot drift from each other.
 */

/**
 * Whether approving this candidate is the last gate before it is live. `undefined` reads as `true`:
 * an older API, or a candidate whose policy snapshot predates the flag, describes what every edge did
 * before it existed — an approval that deploys.
 */
export function approvalDeploys(candidate: Pick<PromotionCandidate, 'deploysOnApproval'>): boolean {
  return candidate.deploysOnApproval !== false;
}

/** One sentence for a confirmation dialog, where the promotion is already named above it. */
export function approvalEffectSentence(deploys: boolean, targetEnv: string): string {
  return deploys
    ? ` Approving records your sign-off. The gate here is the only gate — once it is satisfied this deploys to ${targetEnv} automatically.`
    : ` Approving records your sign-off. Even once the gate is satisfied this does not ship: the deployment pipeline stops at an approval of its own before ${targetEnv} changes.`;
}
