# The initiator cannot approve — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring back the rule "the person who initiated a promotion may not approve it" as a switch on
the promotion policy of an edge: on by default for edges into production, off for the others, and an
admin can turn it on or off per policy.

**Architecture:** `PromotionPolicy` gains `InitiatorCannotApprove`. `PromotionPolicyResolver.Project`
copies it into the candidate's `ResolvedPolicySnapshot`, like every other gate flag. The initiators
are taken from the participants recorded **on the candidate**, as the D17 note in
`PromotionApprovalAuthorizer` asks: payload-driven, with no link to a deploy event. They are frozen
when the candidate is created, so removing yourself as a participant cannot unblock you. The rule is
checked in two places. When an approval is recorded, an initiator is refused. When the gate is
evaluated, approvals and work-item sign-offs by an initiator do not count. The second check covers
snapshots refreshed after a policy edit, and sign-offs that are shared across candidates. A promotion
the portal creates by itself (from a registered build) is initiated by **the system**: the candidate
records that (`InitiatedBySystem`), no person is its initiator, and the switch blocks nobody on it.

**Tech Stack:** ASP.NET Core net10.0 Minimal API + EF Core (Postgres & SQL Server migrations), xUnit + NSubstitute, React 19 + TypeScript.

**Spec:** the owner's decision of 2026-11-23, summarised here. It reverses **D17** of
`docs/plans/external-promotion-creation.md`:

> | D17 | Separation of duties (`ExcludeRole`) | **Removed for now** — anyone authorized for a requirement may approve, incl. the deploy scheduler. Re-add later as payload-driven | §4.1, §8.2, §8.3 |

D17 was recorded as a row in that plan's decision table, not as an ADR. Reversing it is therefore
recorded the same way, in this plan's Decisions, with a note on the D17 row (Task 7); no ADR is
added. The plan's §6.2 item 2 ("Payload-driven separation of duties (deferred, D17)") already
described this re-add, which this plan carries out.

## Global Constraints

- Payload-driven: initiators come from `PromotionCandidate.Participants` (and the creator stamped
  there); nothing reads deploy events. The only other input is whether the portal itself created the
  candidate (`InitiatedBySystem`, Decision 10), which a caller cannot set.
- The switch narrows who may **approve**. Reject, cancel an approval, comment, assign participants,
  the admin bypass and the admin reject are unchanged.
- Nothing that passes today starts failing on an edge whose policy has the switch off. Turning it
  on applies only to candidates whose snapshot carries it (see Decision 6).
- Every schema change ships a Postgres and a SQL Server migration (`Migrations/Postgres`,
  `Migrations/SqlServer`); portable SQL only.
- Rollback requests reuse `ResolvedPolicySnapshot` (`RollbackPolicyResolver`). The new field
  defaults to `false` there, so rollbacks are unaffected (see Decision 9).
- No commits on `main`, no push, no PR without the user's consent.

## Decisions

1. **Who counts as the initiator.** These people:
   - every participant on the candidate whose role is `triggered-by` or `author`. Roles are matched
     after `RoleNormalizer.Normalize` and the configured role aliases (`RoleAliasMap`), because
     participants are stored canonicalised;
   - the person who created the promotion when a person did. `POST /api/promotions/from-build` and
     the MCP `promote_build` tool already stamp the caller as `triggered-by` (`PromotionEndpoints.cs`,
     `Mcp/PromotionTools.cs`), so that creator is covered by the first rule. `POST /api/promotions`
     is API-key only (`ApiKeyAuthHandler.PolicyName`). A key is not a person, so there the
     initiators are whoever the payload names with those roles. The candidate stores no other
     "created by" field;
   - **nobody**, when the portal created the promotion by itself: the system is the initiator
     (Decision 10).
2. **Reference participants do not count** (recommendation, adopted). A PR's author, a ticket's
   QA or the participants of a work-item reference are not initiators. A promotion bundles many
   changes. Counting every PR author would make everyone whose code is in a release unable to approve
   it, which on small teams leaves nobody, and it is not what separating duties asks: the person who
   *initiates* the move does not also *clear* it. A pipeline that wants the change's author excluded
   sends that person as a promotion-level `author` participant.
3. **Frozen at creation, widened later.** `PromotionCandidate.InitiatorsJson`
   (`[{ role, displayName, email }]`) is filled once from the participants when the candidate is
   created. The initiator set at evaluation is that frozen list **plus** the current participants
   with those roles. Adding an initiator later restricts the gate; removing a participant never
   unblocks it.
4. **Matching addresses.**
   - For the signed-in caller, `ICurrentUser.IsMe(email)` is used
     (`Infrastructure/Auth/ICurrentUser.cs`, `CurrentUserEmails`). It covers every address the
     directory knows for them, so a sign-in name that differs from the mail recorded in the payload
     still matches.
   - **An initiator without an email cannot be matched** (`PromotionParticipant.Email` is nullable,
     and the MCP create paths send `null` for a caller that has none). Such an entry is kept in
     `InitiatorsJson` as an **unidentified initiator**: it blocks nobody, because nobody can be shown
     to be that person, and it is never silently dropped. The approval progress panel says
     "Initiator not identified: <display name or role>", diagnostics report
     `initiator_unidentified`, and the gate otherwise works as for a promotion with no initiator
     (Decision 11). A signed-in caller always has an address (`ICurrentUser.Email`), so a person
     who creates a promotion in the UI or with a personal token is always identified; only
     pipelines and keys can produce an unidentified initiator.
   - For recorded approvals and sign-offs, the stored address is compared in the canonical form
     approvals are stored in (`PromotionService.NormalizeEmail`: trim, lower-case). Other people's
     addresses cannot be widened (`SamePersonAs`). The record-time check is therefore the strong
     one, and the evaluation-time filter is a second line.
5. **An Admin personal token is its owner.** A decision made through a personal access token
   (`PersonalTokenAccess.Admin` on `POST /{id}/approve`, `PersonalTokenRestrictions`) is the
   owner's, so the owner's initiator status applies. Being an admin does not exempt anyone: the admin
   shortcut in `PromotionApprovalAuthorizer.IsInApproverGroupAsync` makes an admin *eligible*, and the
   initiator check runs after it. An admin who initiated a promotion and must clear it uses
   **Bypass**, which stays audited and reason-carrying.
6. **When it applies.** The switch is read from the candidate's snapshot, so a candidate follows the
   policy as it was when it was created. As today, an admin's edit to the policy refreshes the
   snapshots of Pending candidates (`PromotionService.RefreshPolicySnapshotsAsync`); turning the
   switch on then applies to them too. Approvals an initiator already recorded stop counting, but
   they are kept as history.
7. **What counts as a production edge.**
   - An edge is a production edge when its target environment is marked **Production** in
     **Settings → Environments** (`EnvironmentConfigDto.IsProduction`).
   - An unconfigured target counts when its key reads as production
     (`EnvironmentStage.IsProductionByName`), the rule `ReleaseService` already uses for "the
     production environment".
   - Aliases resolve first (`EnvironmentAliasResolver`).
8. **Defaults.**
   - The column defaults to `false`.
   - A one-off data step (`InitiatorRuleBackfill`) runs at startup after the migration and sets it to
     `true` on every existing policy whose target is a production environment. It records that it ran
     in `platform_settings` (`promotions.initiatorRuleBackfilled`) and never runs again. It is
     C# because the production flag lives in the stored settings, which SQL may not read (no JSON
     functions).
   - It does not refresh Pending candidates' snapshots, so promotions already waiting are not
     blocked overnight.
   - A new policy whose request omits the field gets `true` when its target is a production
     environment, otherwise `false`. Marking an environment Production later changes no policy: the
     editor shows a hint instead.
9. **Rollbacks are out of scope.** Rollback requests have their own policy and an admin override;
   the owner's decision names promotions.
10. **The system initiates the promotions the portal creates by itself** (owner, 2026-11-23). No
    person is their initiator, so the switch blocks nobody on them; anyone eligible may approve.
    - **Which paths.** Read from the code, every candidate goes through
      `PromotionService.CreateExternalCandidateAsync`. Its callers are:

      | Caller | Who initiates |
      |---|---|
      | `BuildIngestHook.OnRegisteredAsync` (`Features/Builds/BuildIngestHook.cs`): on build registration, one candidate per `build → *` edge whose policy's `AutoCreateFromBranches` matches the build's branch. It sends `Participants: null`. | **The system** |
      | `POST /api/promotions/from-build` (`PromotionEndpoints.cs`) and MCP `promote_build` | The caller, stamped `triggered-by` |
      | MCP `create_promotion` (`Mcp/PromotionTools.cs`) | The caller, stamped `triggered-by` |
      | `POST /api/promotions` (API key) | Whoever the payload names (Decision 1), else nobody identified (Decision 11) |

      `PromotionIngestHook` creates no candidate: since D18/D19 it only closes (`MatchCompletionAsync`)
      and supersedes (`SupersedeOvertakenAsync`) them on deploy events. "Auto-create from branches"
      is the `BuildIngestHook` path above, not a separate one. So today the only system-initiated
      path is build registration; a later path that creates candidates by itself (a deploy event,
      a schedule, a release train) passes the same marker.
    - **How it is recorded.** `CreateExternalCandidateAsync` gains a parameter
      `PromotionInitiator initiator = PromotionInitiator.Caller`; `BuildIngestHook` passes
      `PromotionInitiator.System`. It is a code parameter, not a field of `CreatePromotionDto`, so no
      API or MCP caller can claim to be the system. A new candidate stores it as
      `PromotionCandidate.InitiatedBySystem = true`. No participant is invented for it: a
      `triggered-by` "system" entry without an email would read as an unidentified initiator
      (Decision 4), and it would appear among the participants in the UI and the API.
    - **Set at creation only.** When the natural-key reuse (D15) updates an existing candidate, the
      flag does not change: a build registration replay over a candidate a person opened keeps it
      person-initiated. A person who later opens the same promotion from the build (from-build reuses
      the system's candidate and records them as `triggered-by`) becomes an initiator by Decision 3,
      because they asked for that move.
    - **What it shows.** The approval progress panel says "Initiated by the system"; the detail and
      list carry `initiatedBySystem`; diagnostics report `initiated_by_system` and never select the
      initiator cause for it.
    - **Existing candidates** keep `false`. Those the hook created before this change have no
      participants and read as "initiator not identified" (Decision 11), which blocks nobody either,
      so no backfill guesses at their origin.
11. **API promotions without an identified initiator are not refused** (owner, 2026-11-23). A
    `POST /api/promotions` with no participants, or whose initiators have no email, is accepted as
    today, also on an edge whose policy has the switch on. The switch does not apply to it, and it is
    shown as "Initiator not identified" (Decision 4). Refusing them would break pipelines that pass
    today; the owner may revisit this, and would then need a warning period.

## Review Focus

1. **The initiator can never clear the gate**, by any path:
   - a single approve, `ApproveGateAsync`, and Mass approve (`POST /bulk/approve`);
   - signing off every work item (`WorkItemApprovalService.ApproveAllOnCandidateAsync`) under
     `AutoApproveOnAllWorkItemsApproved` or `RequireAllWorkItemsApproved`;
   - a single sign-off (`POST /api/work-items/{key}/approvals`), which is shared by every candidate of
     that key, product, service and target environment;
   - an approval or sign-off recorded **before** the switch was turned on.
   
   → Tasks 3, 4.
2. **Removing yourself as a participant does not help** (`DELETE /{id}/participants/{role}` after
   creation). → Task 3.
3. **Reject stays possible for the initiator.** `RejectAsync` calls `EnsureUserCanApproveAsync`, so
   the new check must not be added there. → Task 3.
4. **The list and the detail agree.** `CanUserApproveAsync`, `CanUserApproveManyAsync` and
   `GetApprovableRequirementsManyAsync` say "no" exactly when `ApproveAsync` would refuse, so
   **Awaiting your approval**, My Tasks and the bulk selection do not offer it. → Task 3.
5. **Defaults.** Only production-target policies are switched on by the backfill, and a second
   startup changes nothing. → Task 2.
6. **The system as initiator.** Only `BuildIngestHook` marks a candidate as system-initiated; no
   request body can set it; a replay never changes it; a person recorded later is still an
   initiator. → Tasks 1, 3.

---

### Task 1: Entity, snapshot, DbContext, migrations

**Files:** `Features/Promotions/Models/PromotionPolicy.cs`, `Models/ResolvedPolicySnapshot.cs`,
`Models/PromotionCandidate.cs`, `PromotionPolicyResolver.cs` (`Project`),
`Infrastructure/Persistence/PlatformDbContext.cs`, new migrations
`AddPromotionPolicyInitiatorCannotApprove` under `Migrations/Postgres` and `Migrations/SqlServer`.

- `PromotionPolicy.InitiatorCannotApprove : bool = false`.
- `ResolvedPolicySnapshot.InitiatorCannotApprove { get; init; } = false`, so older snapshots
  deserialise to "off", in keeping with the record's rule for new members.
- `PromotionPolicyResolver.Project` copies the field. It is the only policy → snapshot projection,
  and seed data uses it too.
- `PromotionCandidate.InitiatorsJson : string = "[]"` with a `List<PromotionParticipant> Initiators`
  accessor (same pattern as `ParticipantsJson`).
- `PromotionCandidate.InitiatedBySystem : bool = false` (Decision 10), and a new
  `enum PromotionInitiator { Caller, System }` in `Features/Promotions/Models`.
- The migration adds the three columns with defaults (`false`, `'[]'`, `false`). The companion startup step fills
  `InitiatorsJson` of existing Pending candidates from their current participants.

Tests (`PromotionPolicyResolverTests`): `Project_CopiesInitiatorCannotApprove`;
`OldSnapshotJson_ReadsTheSwitchAsOff`.

### Task 2: Defaults and the backfill (TDD)

**Files:** new `BackgroundServices/InitiatorRuleBackfillService.cs` (a one-shot `BackgroundService`
like `DeployEventWorkItemBackfillService`), with the logic in `Features/Promotions/InitiatorRuleBackfill.cs`; `PromotionAdminEndpoints.cs` (create and update, `MapPolicy`); a shared
`ProductionEnvironments.IsProductionTarget(key, settings)` built on `EnvironmentStage` and
`EnvironmentAliasResolver`.

Tests (new `InitiatorRuleBackfillTests`; `PromotionPolicyGateTests` extended):
- `Backfill_SwitchesOnPoliciesIntoProduction_Only`;
- `Backfill_UsesTheProductionFlag_AndTheNameFallback_AndAliases`;
- `Backfill_RunsOnce`;
- `Backfill_DoesNotRefreshPendingSnapshots`;
- `CreatePolicy_WithoutTheField_DefaultsByTarget`;
- `UpdatePolicy_WithoutTheField_KeepsIt`;
- `UpdatePolicy_TurningItOn_RefreshesPendingSnapshots`.

### Task 3: Enforcement (TDD)

**Files:**
- `PromotionService.cs`:
  - initiators are stamped in `CreateExternalCandidateAsync`, which gains
    `PromotionInitiator initiator = PromotionInitiator.Caller` and sets `InitiatedBySystem` on a new
    candidate only (not in `UpdateExistingCandidateAsync`);
  - new `IsInitiator(candidate)` for the current user, and `InitiatorEmails(candidate)`;
  - in `ApproveAsync`, after `EnsureUserCanApproveAsync`, throw `InitiatorCannotApproveException`
    when the snapshot has the switch on and the caller is an initiator;
  - `GetEligibleRequirementsAsync` is unchanged (the card still shows);
  - `CanUserApproveAsync`, `GetApprovableRequirementsManyAsync` and so `CanUserApproveManyAsync`
    return "nothing approvable" for an initiator;
  - `EvaluateGateAsync` drops approvals whose `ApproverEmail` is an initiator's before
    `ApprovalMatcher` runs.
- `WorkItemApprovalService.cs`:
  - in the gate's sign-off read (the overall status behind `RequireAllWorkItemsApproved` and
    `AutoApproveOnAllWorkItemsApproved`), an initiator's sign-off that would
    clear the gate counts as not signed. Today both `Approved` and `Issue` clear it
    (`WorkItemOverallStatus.ClearsGate`); only `Blocked` holds. So an initiator's `Approved` **and**
    `Issue` are not counted for their own promotion — otherwise marking an issue would be a way to
    clear one's own gate — while their `Blocked` still holds it. For everyone else nothing changes:
    an issue still clears, a block still holds. The ticket stays undecided for that promotion
    until someone who is not an initiator rules on it;
  - `ApproveAllOnCandidateAsync` refuses for an initiator;
  - the single sign-off is still recorded, because it may clear the same ticket for someone else's
    promotion. Its response gains `notCountedFor: [candidateId]` for the candidates the signer
    initiated.
- `PromotionEndpoints.cs`:
  - `POST /{id}/approve` and `/bulk/approve` map the exception to `403` with
    `code: "initiator_cannot_approve"`, per row on bulk;
  - the detail gains `initiatorCannotApprove`, `initiators`, `youAreInitiator`, and `canApprove`
    false for them.
- `Features/Builds/BuildIngestHook.cs`: `CreateCandidateAsync` passes `PromotionInitiator.System`.
  The from-build endpoint and the MCP tools keep the default.
- `BypassAsync` writes `initiator: true` into its audit entry when the admin is an initiator;
  `AdminRejectAsync` is unchanged.
- The detail and list gain `initiatedBySystem`.

Tests (`PromotionServiceTests`, integration `PromotionCanApproveTests`,
`PromotionMassApproveTests`, `WorkItemApprovalTests`, `PromotionApproveAllWorkItemsTests`,
`PromotionPolicyReapplyTests`):

**Recording an approval**
- `Initiator_CannotApprove_WhenTheSwitchIsOn`;
- `Initiator_CanApprove_WhenTheSwitchIsOff`;
- `TriggeredBy_And_Author_AreInitiators_OtherRolesAndReferenceParticipantsAreNot`;
- `InitiatorMatchedByAnyOfTheirDirectoryAddresses`;
- `RemovingTheInitiatorParticipant_DoesNotUnblock`;
- `AddingAnInitiatorLater_Blocks`;
- `Initiator_CanStillReject_AndCancel`;
- `AdminInitiator_IsRefused_ButCanBypass_AndTheBypassIsAuditedAsInitiator`;
- `AdminPersonalToken_ActsAsItsOwner`.

**Mass approve and work items**
- `MassApprove_SkipsTheInitiatorsRows_WithTheReason`;
- `ApproveAllWorkItems_IsRefusedForTheInitiator`;
- `SignOffByTheInitiator_DoesNotClearTheirPromotion_ButClearsAnothers`.

**Gate evaluation**
- `ApprovalRecordedBeforeTheSwitch_StopsCountingAfterTheRefresh`;
- `CanApproveMany_IsFalseForTheInitiator`;
- `OnlyEligibleApproverIsTheInitiator_StaysPending`.

**The system as initiator** (`BuildIngestHookTests`, `PromotionServiceTests`, integration
`PromotionCanApproveTests`)
- `BuildAutoCreate_RecordsTheSystemAsInitiator_AndNoParticipant`;
- `SystemInitiated_AnyEligibleApproverCanApprove_WhenTheSwitchIsOn`;
- `FromBuild_And_McpCreate_And_ApiCreate_AreNotSystemInitiated`;
- `CreatePromotionDto_CannotSetTheSystemMarker` (an extra JSON field is ignored);
- `RegistrationReplay_OverAPersonsCandidate_KeepsItPersonInitiated`;
- `FromBuild_OverTheSystemsCandidate_MakesThePersonAnInitiator`;
- `Detail_SaysInitiatedBySystem`.

**No identified initiator** (Decision 11)
- `ApiCreate_WithoutParticipants_IsAccepted_WhenTheSwitchIsOn`;
- `ApiCreate_InitiatorWithoutEmail_IsAccepted_AndShownAsNotIdentified`.

### Task 4: Diagnostics

**Files:** `Features/Diagnostics/Observations.cs` (`InitiatorCannotApprove`,
`InitiatorApprovalIgnored`), `DiagnosticsService.DiagnosePromotionAsync`,
`playbooks/promotion-stuck-pending.yaml` (new cause `initiator-is-the-only-approver`).

- `initiator_cannot_approve` is emitted when the snapshot has the switch on and an initiator is in
  the explicit user list of an open requirement. The cause applies when that requirement has no
  group and no other user. Group membership of other people cannot be listed, as the
  `GetApprovalsAsync` note explains, so a group-only requirement is reported as "an initiator may be
  the only member".
- `initiator_approval_ignored` is emitted when a recorded approval or sign-off was not counted
  because its author is an initiator.
- `initiated_by_system` (informational) is emitted when the candidate is system-initiated with the
  switch on: the rule blocks nobody here. The initiator cause is never selected for it.
- The cause reads: "The person who started this promotion cannot approve it, and nobody else can
  clear this gate." The fix: "Ask another approver, add one to the policy, or have an administrator
  bypass the gate with a reason."

Tests (`DiagnosticsServiceTests` or the integration diagnostics test): both observations, and the
cause selected only for an initiator-only requirement.

### Task 5: UI

**Files:**
- `src/Platform.Web/src/lib/api.ts` and `lib/types.ts`: the fields.
- `app/settings/PromotionSettings.tsx`: a checkbox **The initiator cannot approve** with the help
  text "The person who started a promotion (Triggered by or Author) cannot approve it.". It defaults
  to checked for a production target. A production edge with it off shows a badge in the policy list.
- `app/promotions/PromotionDetailPage.tsx`: `approveBlockedReason` gains the case "You started
  this promotion, and this edge's policy does not let the person who started a promotion approve
  it." The Approve button renders disabled with that reason; the card stays.
- `components/approvals/ApprovalProgress.tsx`: a line "The initiator cannot approve: <names>";
  "Initiated by the system" for a system-initiated candidate; "Initiator not identified: <display
  name or role>" as in Decision 4.
- `components/promotions/MassApproveDialog.tsx`: rows refused with `initiator_cannot_approve` say
  "You started this promotion".
- `components/promotions/ApproveAllWorkItems.tsx`: disabled with the same reason.

Checked in the dev server as an initiator and as another approver, on a production and a
non-production edge.

### Task 6: Endpoints and API docs

- Admin policy endpoints take `initiatorCannotApprove?`.
- The candidate detail and list carry `initiatorCannotApprove`, `initiators`, `initiatedBySystem` and
  `youAreInitiator`.
- `POST /api/promotions` is unchanged: no field marks a promotion as system-initiated, and a request
  without an identified initiator is accepted (Decision 11).
- `403 initiator_cannot_approve` on approve and bulk approve.
- The single sign-off response carries `notCountedFor`.

### Task 7: Docs

- `notes/promotions-api.md`:
  - the policy field and its default;
  - who counts as the initiator, and that a promotion the portal opens from a build is initiated
    by the system, so the rule blocks nobody on it;
  - that a create without an identified initiator is accepted and the rule does not apply to it;
  - the `403` code and the new response fields;
  - that a pipeline sending participants decides who the initiators are.
- `.claude/skills/infrapilot-api/SKILL.md`: an approval through a token can now be refused for the
  token's owner as initiator, and diagnose_promotion names it.
- `GLOSSARY.md`, a new term:
  > **Initiator** (of a promotion): A person recorded on the Promotion Candidate as `triggered-by` or
  > `author`, including whoever created it from the portal. When the edge's Approval Policy says so,
  > an Initiator cannot approve it. A Promotion Candidate the portal creates by itself (from a
  > registered build) is initiated by the system and has no Initiator.
  > _Avoid_: Requester, owner, author (for the whole set)
- `docs/plans/external-promotion-creation.md`: a note under the D17 row and §6.2 item 2: "Reversed
  2026-11-23 by `docs/plans/no-self-approval-switch.md`".
- The code comments that say "ExcludeRole was removed (D17)" (`PromotionApprovalAuthorizer.cs`,
  `PromotionService.EnsureUserCanApproveAsync`) point at the new check.

### Task 8: What's new entry

`src/Platform.Web/src/app/whats-new/entries/YYYY-MM-DD-initiator-cannot-approve.md`, dated the day
it merges:

```markdown
---
title: The person who started a promotion cannot approve it
date: YYYY-MM-DD
audience: approvers
link: /promotions
linkLabel: Promotions
---
On edges into production, the person who started a promotion can no longer approve it: their
**Approve** button is disabled and says why, and the approval progress shows who cannot approve.
Another approver has to clear it. Promotions the portal opens by itself from a new build show
**Initiated by the system**, and any approver can clear them. Admins can turn this on or off per edge
in **Settings → Promotions** with **The initiator cannot approve**.
```

### Task 9: Verify

`dotnet test` both projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`; both migration
sets on a fresh database and on a copy with policies (the backfill sets exactly the production-edge
policies); the web dev server with `/whats-new` open; the manual checks of Task 5.

## Relation to the governance rules

The governance rules require separation of duties for production releases: whoever initiates a
release into production must not be the one who approves it. With the default on for production
edges, the portal now enforces that rule instead of relying on people. An admin who turns the switch
off on a production edge departs from the governance rules. The change is audited like every policy
edit, and the policy list marks such an edge. The portal does not refuse the change: which edges
count is configuration, and the governance rules, not the portal, are the authority. A promotion the
portal opens by itself has no person as initiator, so any eligible approver clears it without
departing from the rule. A promotion created through the API without an identified initiator is
accepted (Decision 11); separation of duties then rests on the pipeline naming its initiator.

## Open points

None. Open point 1 (who initiates an API-key promotion with no participants, or with initiators
without an email, and whether to refuse it) was decided by the owner on 2026-11-23: it is not
refused, the switch does not apply to it, and it is shown as "Initiator not identified" (Decision 11).
