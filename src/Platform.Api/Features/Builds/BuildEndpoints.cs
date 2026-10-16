using System.Security.Claims;
using Platform.Api.Features.Builds.Models;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Builds;

public static class BuildEndpoints
{
    public static RouteGroupBuilder MapBuildEndpoints(this RouteGroupBuilder group)
    {
        // Registration — called by publish pipelines, secured like deploy ingest: API key +
        // per-key rate limit + product scope, plus the build:register scope for keys that
        // declare a Scopes list. The producer treats a non-2xx as a stage failure (D11), so
        // this endpoint must be idempotent for retries — see BuildService.RegisterAsync.
        group.MapPost("/", async (BuildService service, ClaimsPrincipal user, RegisterBuildDto dto, CancellationToken ct) =>
        {
            if (!ApiKeyAuthHandler.HasScope(user, ApiKeyScopes.BuildRegister))
                return Results.Forbid();

            var errors = Validate(dto);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            // Enforce product scope when the key restricts which products it can post for. Checked
            // against the product the key SENT, not the one a ServiceProductOverride redirects it to:
            // the claim says what this key is entitled to talk about, and the redirect is an admin
            // decision the key neither chose nor can influence. Scoping on the resolved product would
            // instead break every pipeline whose key still names the product it is migrating off.
            var allowedProducts = user.FindAll(ApiKeyAuthHandler.AllowedProductClaim).Select(c => c.Value).ToList();
            if (allowedProducts.Count > 0 &&
                !allowedProducts.Contains(dto.Product, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Forbid();
            }

            var result = await service.RegisterAsync(dto, ct);
            var body = new { result.Build.Id, result.Build.Version, result.Build.Branch, result.Replayed };
            // Replay of an already-registered build (same product+service+version) → 200 with the
            // updated row, so retrying senders can distinguish "created" from "already there" but
            // treat both as success.
            return result.Replayed
                ? Results.Ok(body)
                : Results.Created($"/api/builds/{result.Build.Id}", body);
        })
        .RequireAuthorization(ApiKeyAuthHandler.PolicyName)
        .RequireRateLimiting(DeploymentIngestionRateLimit.PolicyName);

        // List for the UI picker and the registry page. Newest first. `q` is the free-text search
        // (case-insensitive substring across product, service, version, branch, commit and CI build
        // id — "aws" finds swo-extension-aws); the named fields identify instead of search, so
        // product/service match exactly and `version` pins ONE build, which is what a promotion's
        // "built from …" link needs. `branch` stays a substring match. `since`/`until` window the
        // registration time (since inclusive, until exclusive).
        // Accepts both ?service= and ?serviceName= — the rest of the API says serviceName,
        // the plan's read surface says service.
        group.MapGet("/", async (
            BuildService builds, string? product, string? service, string? serviceName, string? branch,
            string? version, string? q, DateTimeOffset? since, DateTimeOffset? until, int? limit,
            CancellationToken ct) =>
        {
            var results = await builds.ListAsync(
                new BuildQuery(product, service ?? serviceName, branch, version, q, since, until),
                limit is > 0 and <= 200 ? limit.Value : 50, ct);
            return Results.Ok(new { results });
        });

        // Pick lists for the page's filter combo boxes, counted against the same filters the list
        // applies (each facet minus its own field). Takes the identical query string as the list
        // above, so the page can send one filter state to both.
        group.MapGet("/facets", async (
            BuildService builds, string? product, string? service, string? serviceName, string? branch,
            string? version, string? q, DateTimeOffset? since, DateTimeOffset? until, int? limit,
            CancellationToken ct) =>
        {
            var facets = await builds.FacetsAsync(
                new BuildQuery(product, service ?? serviceName, branch, version, q, since, until),
                limit is > 0 and <= 500 ? limit.Value : 200, ct);
            return Results.Ok(facets);
        });

        // Single build including its manifest.
        group.MapGet("/{id:guid}", async (BuildService builds, Guid id, CancellationToken ct) =>
        {
            var build = await builds.GetAsync(id, ct);
            return build is null ? Results.NotFound() : Results.Ok(build);
        });

        return group;
    }

    private static List<string> Validate(RegisterBuildDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Product)) errors.Add("'product' is required");
        if (string.IsNullOrWhiteSpace(dto.Service)) errors.Add("'service' is required");
        if (string.IsNullOrWhiteSpace(dto.Version)) errors.Add("'version' is required");
        // Branch is what makes the registry answer "which branch produced this?" — a registration
        // without it would defeat the point, so it is rejected rather than defaulted.
        if (string.IsNullOrWhiteSpace(dto.Branch)) errors.Add("'branch' is required");
        return errors;
    }
}
