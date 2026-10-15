using System.Security.Claims;
using System.Threading.RateLimiting;
using ModelContextProtocol.Protocol;
using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Mcp;

/// <summary>
/// InfraPortal's MCP server: the deployments, promotions, builds, work items and diagnostics the
/// portal holds, as tools a coding agent (Claude Code, Copilot, any MCP client) can call.
///
/// <para>It lives inside the API rather than beside it so the tools call the same services the
/// endpoints do — one copy of every rule — and so they reach what REST does not expose, such as the
/// diagnosis playbooks. Mounted under <c>/api</c> because that is what the production container's
/// nginx forwards to the API (infra/nginx-single.conf); anything else there is served the SPA.</para>
///
/// <para>Authentication is whatever the API groups accept: an <c>X-Api-Key</c> (a configured key or
/// a personal token) or a bearer token. Stateless: every request stands alone, so it balances across
/// replicas with no session affinity, and a tool runs inside the HTTP request that asked for it —
/// <see cref="ICurrentUser"/> and the claims-based checks see the caller.</para>
/// </summary>
public static class McpServerSetup
{
    public const string Path = "/api/mcp";
    public const string RateLimitPolicy = "Mcp";

    public static IServiceCollection AddInfraPortalMcp(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<McpContext>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "infraportal",
                    Title = "InfraPortal",
                    Version = typeof(McpServerSetup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<AccessTools>()
            .WithTools<DeploymentTools>()
            .WithTools<PromotionTools>()
            .WithTools<WorkItemTools>()
            .WithTools<BuildTools>()
            .WithTools<DiagnosticTools>()
            .WithTools<AnalyticsTools>();

        // One bucket per caller, apart from the ingestion limiter: an agent in a loop should run out
        // of its own budget, not the budget of the pipeline whose key it happens to share a name with.
        var permitLimit = Math.Max(1, config.GetValue("Mcp:RateLimit:PermitLimit", 300));
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            var caller = context.User.FindFirstValue("oid")
                ?? context.User.FindFirstValue("sub")
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? "anonymous";
            return RateLimitPartition.GetSlidingWindowLimiter(caller, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            });
        }));

        return services;
    }

    public static IEndpointConventionBuilder MapInfraPortalMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path)
            .RequireAuthorization(AuthorizationPolicies.CanApprove)
            .RequireRateLimiting(RateLimitPolicy);

    /// <summary>
    /// Sent to the client on initialize. What a model needs to use the tools well and cannot learn
    /// from any one tool's description — kept short, because clients put it in every prompt.
    /// </summary>
    private const string Instructions = """
        InfraPortal tracks what is deployed where (product → service → environment), promotions that move a
        version from one environment to the next under an approval gate, the build registry, and the work
        items (tickets) that gate promotions.

        - Names: products and services are kebab-case ("marketplace", "mpt-extension-adobe"). Tools accept
          near-misses and fragments and report what they assumed in `nameResolution` — pass that on. Use
          find_services when you know a service but not its product.
        - Environments are canonical keys; aliases such as "prod" resolve automatically.
        - Deploy events, promotions and builds are addressed by GUID; list and search tools return them.
        - Records carry a `url` into the portal. Give the user that link; never build one yourself.
        - When something looks wrong — a promotion not moving, a failed deploy — call diagnose_promotion or
          diagnose_deployment first: they apply the platform's playbooks and cite the evidence. For how the
          release pipelines behave, call search_knowledge; they are installation-specific, so do not answer
          from general knowledge.
        - Promotion lifecycle: Pending → Approved → Deploying → Deployed, or Rejected / Superseded. A
          succeeded deploy of the version to the target closes it. `fromVersion` is what the target ran when
          the promotion was created; `targetCurrentVersion` is what it runs now.
        - Approving or rejecting promotions and signing off work items are human decisions made in the
          portal; no tool does them. Write tools act as the caller — call whoami to see what it may do.
        - Times are UTC ISO-8601.
        """;
}
