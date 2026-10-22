# Promotion Freeze per service — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an administrator freeze either a whole product's promotions into one environment (as today) or only one service of that product into that environment.

**Architecture:** The freeze key grows from `(Product, TargetEnv)` to `(Product, Service, TargetEnv)` with the sentinel `""` meaning "whole product" (ADR-0003). Matching is a **union**: a candidate `(p, s, env)` is held when a freeze `(p, "", env)` **or** `(p, s, env)` exists; there is no precedence between the two shapes. Everything else (gate hold, lift sweep, webhooks, audit, journal, UI) carries the optional service through.

**Tech Stack:** ASP.NET Core net10.0 Minimal API + EF Core (Postgres & SQL Server migrations), xUnit + NSubstitute, React 19 + TypeScript.

**Spec:** the task brief from the parent session (summarised here); decisions in `docs/adr/0003-freeze-may-be-scoped-to-one-service.md` and `GLOSSARY.md`.

## Global Constraints

- Sentinel for "whole product" is the empty string `""`, never NULL (unique index semantics differ between Postgres and SQL Server).
- Public/API shapes expose the service as `service: string | null` (`null` = whole product).
- `FindFreezeAsync` returns the product-level freeze before the service-level one when both exist.
- Build: `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet`; restore with `--source https://api.nuget.org/v3/index.json --ignore-failed-sources`.
- No commits on `main`, no push, no PR without the user's consent.

## Review Focus

1. Product freeze + service freeze on the same pair: lifting one must leave the other holding; `GetHeldByFreezeAsync` and the detail `freeze` must still report held. → Task 2/3 tests.
2. A service freeze must not hold candidates of sibling services, and its lift sweep must not re-evaluate them. → Task 2/3 tests.
3. Freezing a service with only a product-level policy (`Service = null`) must be allowed (422 only when neither shape exists). → Task 3 tests.
4. Legacy audit entries without `service` must still parse as product-level journal/windows. → Task 4 tests.
5. `DELETE /freezes/{product}/{targetEnv}` must lift only the product-level freeze, never a service one. → Task 5 integration test.

---

### Task 1: Entity, DbContext, migrations

**Files:** `Models/PromotionFreeze.cs`, `PlatformDbContext.cs`, new migrations `AddPromotionFreezeService` under `Migrations/Postgres` and `Migrations/SqlServer`.

- `PromotionFreeze.Service : string = ""` (max 200, required, default `""`).
- Unique index `(Product, Service, TargetEnv)` replaces `(Product, TargetEnv)`.
- Migration adds the column with `defaultValue: ""` (fills existing rows), drops the old index, creates the new one.

### Task 2: Matching rule (TDD)

**Files:** `PromotionService.cs` (`FindFreezeAsync(product, service, targetEnv, ct)` + 4 callers), `PromotionEndpoints.cs` (detail caller), `PromotionFreezeService.cs` (`GetHeldByFreezeAsync`, `CountHeldAsync`).

Tests (`PromotionFreezeTests`): service freeze holds only its service; product freeze holds all; both at once → product returned first; `GetHeldByFreeze` with both key shapes; `GetActive` held count for a service freeze counts only that service.

### Task 3: FreezeAsync / LiftAsync (TDD)

`FreezeAsync(product, targetEnv, reason, service = null)`: 422 `NoApprovalPolicyForPairException` unless a policy `(p, s, env)` or `(p, null, env)` exists; 422 `UnknownServiceException` when `s` is on no candidate and no deploy event of the product; 409 per triple. `LiftAsync(product, targetEnv, service = "")` keyed by triple; sweep limited to the service for a service freeze. `GetKnownServicesAsync(product)` shared by validation and the UI endpoint.

### Task 4: Webhooks, audit payload, history/windows parser (TDD)

Payload gains `service` (null for product); `WebhookEventFilters.Service` set for service freezes. `ActiveFreeze`, `FreezeHistoryEntry`, `FreezeWindow` gain `string? Service`. Parser reads `service` (absent/empty → null).

### Task 5: Endpoints + integration tests

`POST /freezes` body `service?`; `DELETE /freezes/{product}/{targetEnv}` (product) and `DELETE /freezes/{product}/{targetEnv}/services/{service}`; `GET /freezes/services?product=` (known services). Alias resolution as today.

### Task 6: UI

`api.ts` types + calls; `FreezeSettings` service select ("All services" default); cards/journal "product/service → env"; promotions `FreezeCues`/`FreezeNotice` show the service; `FrozenGroup` matches by service; deployments `freezeWindows.ts` matchers take the service so a service freeze marks only its deploys.

### Task 7: Docs

`GLOSSARY.md`, `docs/adr/0003-…`, `notes/promotions-api.md`, `knowledge/webhook-catalogue.yaml`.

### Task 8: Verify

`dotnet test` both projects; `npx tsc -b` and `npm run build` in `src/Platform.Web`; commit on `feature/promotion-freeze-per-service`.
