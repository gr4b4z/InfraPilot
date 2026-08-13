using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Features.Analytics;
using Platform.Api.Features.Approvals;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Promotions;
using Platform.Api.Features.ReleaseNotes;
using Platform.Api.Features.Rollbacks;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Executors;
using Platform.Api.Features.Requests;
using Platform.Api.Features.Users;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure;
using Platform.Api.Infrastructure.Audit;
using Platform.Api.Infrastructure.Features;
using Platform.Api.Infrastructure.FileStorage;
using Platform.Api.Infrastructure.Identity;
using Platform.Api.Infrastructure.Middleware;
using Platform.Api.Infrastructure.Notifications;
using Platform.Api.Agent;
using Platform.Api.BackgroundServices;
using Platform.Api.Infrastructure.Persistence;
using Platform.Api.Infrastructure.AzureDevOps;
using Platform.Api.Infrastructure.Jira;
using Platform.Api.Features.Webhooks;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Platform.Api.Infrastructure.Realtime;

var builder = WebApplication.CreateBuilder(args);

// Telemetry — Application Insights via OpenTelemetry (only when connection string is configured)
var appInsightsCs = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrEmpty(appInsightsCs) && !appInsightsCs.StartsWith('<'))
{
    builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
    {
        options.ConnectionString = appInsightsCs;
    });
}

// Database — provider is selectable via config (Postgres default, SqlServer alternative).
// We register a provider-specific subclass of PlatformDbContext so EF can disambiguate the two
// migration sets (Migrations/Postgres vs Migrations/SqlServer). PlatformDbContext is then
// mapped to whichever subclass was registered.
var dbProvider = (builder.Configuration["Database:Provider"] ?? "Postgres").Trim();
var dbConnectionString = builder.Configuration.GetConnectionString("Platform");

// Retry on transient failures. This matters most at startup: the very first thing the app does is
// apply pending migrations, and in Azure the database is serverless with auto-pause — so the opening
// connection of the first deploy after an idle period has to wait out a cold resume, which takes
// longer than a single connect timeout allows. Without a retry strategy that deploy fails outright
// and the migrations never run.
const int transientRetryCount = 8;
var transientRetryDelay = TimeSpan.FromSeconds(15);

if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<SqlServerPlatformDbContext>(options =>
        options.UseSqlServer(dbConnectionString, sql =>
            sql.EnableRetryOnFailure(transientRetryCount, transientRetryDelay, errorNumbersToAdd: null)));
    builder.Services.AddScoped<PlatformDbContext>(sp => sp.GetRequiredService<SqlServerPlatformDbContext>());
}
else
{
    builder.Services.AddDbContext<PostgresPlatformDbContext>(options =>
        options.UseNpgsql(dbConnectionString, npgsql =>
            npgsql.EnableRetryOnFailure(transientRetryCount, transientRetryDelay, errorCodesToAdd: null)));
    builder.Services.AddScoped<PlatformDbContext>(sp => sp.GetRequiredService<PostgresPlatformDbContext>());
}

// Auth — mode is explicitly configured: "Msal" (Azure AD) or "Local" (DB-based JWT)
var authMode = (builder.Configuration["Auth:Mode"] ?? "Local").Trim();
if (authMode.Equals("Msal", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    // Keep claim names as-issued so CurrentUser (and the rest of the app) can read the
    // literal "roles" claim the same way across MSAL, Local JWT, and API key auth.
    builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.TokenValidationParameters.NameClaimType = "name";
        AcceptHubAccessToken(options);
    });
}
else
{
    // Local DB-based authentication with self-issued JWTs
    var localJwtKey = builder.Configuration["Auth:LocalJwt:Key"] ?? LocalAuthEndpoints.DefaultDevKey;
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Prevent the JWT middleware from remapping claim types (e.g. "roles" → ClaimTypes.Role)
            // so CurrentUser can read them using the original claim names.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = LocalAuthEndpoints.Issuer,
                ValidateAudience = true,
                ValidAudience = LocalAuthEndpoints.Audience,
                ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(localJwtKey)),
                NameClaimType = "name",
                RoleClaimType = "roles",
            };
            AcceptHubAccessToken(options);
        });
}
var isMsal = authMode.Equals("Msal", StringComparison.OrdinalIgnoreCase);
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddPlatformAuthorization(builder.Configuration);
builder.Services.AddDeploymentIngestionRateLimit(builder.Configuration);

// Infrastructure
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IFileStorage, AzureBlobFileStorage>();
builder.Services.AddScoped<IFeatureFlags, FeatureFlags>();
// Scoped, and it memoises per request — the hidden-product set is read by most list queries.
builder.Services.AddScoped<UserPreferencesService>();
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddHttpClient("notification-webhook");
builder.Services.AddSingleton<INotificationChannel, EmailChannel>();
builder.Services.AddSingleton<INotificationChannel, WebhookChannel>();
builder.Services.AddScoped<INotificationService, NotificationDispatcher>();

// Identity / Graph — separate app registration for Graph API (client credentials)
var graphTenantId = builder.Configuration["Graph:TenantId"];
var graphClientId = builder.Configuration["Graph:ClientId"];
var graphClientSecret = builder.Configuration["Graph:ClientSecret"];
if (!string.IsNullOrEmpty(graphTenantId) && !graphTenantId.StartsWith('<')
    && !string.IsNullOrEmpty(graphClientId) && !graphClientId.StartsWith('<')
    && !string.IsNullOrEmpty(graphClientSecret) && !graphClientSecret.StartsWith('<'))
{
    builder.Services.AddSingleton(_ =>
    {
        var credential = new Azure.Identity.ClientSecretCredential(
            graphTenantId, graphClientId, graphClientSecret);
        return new Microsoft.Graph.GraphServiceClient(credential);
    });
    builder.Services.AddScoped<IIdentityService, EntraIdGraphService>();
}
else
{
    builder.Services.AddScoped<IIdentityService, StubIdentityService>();
}

// Azure DevOps
builder.Services.Configure<AzureDevOpsOptions>(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName));
builder.Services.Configure<NormalizationOptions>(builder.Configuration.GetSection(NormalizationOptions.SectionName));
builder.Services.AddHttpClient<AzureDevOpsClient>();

// Jira
builder.Services.Configure<JiraOptions>(builder.Configuration.GetSection(JiraOptions.SectionName));
builder.Services.AddHttpClient<JiraClient>();

// Executors (keyed DI)
builder.Services.AddKeyedScoped<IExecutor, AzureDevOpsRepoExecutor>("azure-devops-repo");
builder.Services.AddKeyedScoped<IExecutor, AzureDevOpsPipelineExecutor>("azure-devops-pipeline");
builder.Services.AddKeyedScoped<IExecutor, GitHubRepoExecutor>("github-repo");
builder.Services.AddKeyedScoped<IExecutor, GitHubActionsExecutor>("github-actions");
builder.Services.AddKeyedScoped<IExecutor, JiraTicketExecutor>("jira-ticket");
builder.Services.AddScoped<ExecutorDispatcher>();

// State Machine
builder.Services.AddScoped<RequestStateMachine>();

// Feature services
builder.Services.AddSingleton<CatalogYamlLoader>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<ApproverResolver>();
builder.Services.AddScoped<DeploymentService>();
builder.Services.AddScoped<Platform.Api.Features.Deployments.ServiceDeletionService>();
builder.Services.AddScoped<ReferenceParticipantOverrideService>();
builder.Services.AddScoped<Platform.Api.Features.Deployments.WorkItemSyncService>();
builder.Services.AddScoped<Platform.Api.Features.Promotions.PromotionPolicyResolver>();
builder.Services.AddScoped<Platform.Api.Features.Promotions.PromotionApprovalAuthorizer>();
builder.Services.AddScoped<Platform.Api.Features.Promotions.PromotionService>();
builder.Services.AddScoped<Platform.Api.Features.Rollbacks.RollbackPolicyResolver>();
builder.Services.AddScoped<Platform.Api.Features.Rollbacks.RollbackService>();
builder.Services.AddScoped<Platform.Api.Features.Promotions.WorkItemApprovalService>();
builder.Services.AddScoped<Platform.Api.Features.Promotions.IPromotionIngestHook, Platform.Api.Features.Promotions.PromotionIngestHook>();
builder.Services.AddScoped<AnalyticsService>();

// Release Notes
builder.Services.AddSingleton<TemplateEngine>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddScoped<ReleaseNoteService>();
builder.Services.AddScoped<ReleaseNoteTemplateService>();

// Shared UI settings (environments, roles, activity template)
builder.Services.AddScoped<Platform.Api.Features.Settings.AppSettingsService>();
// The configured participant-role vocabulary, read from the settings above. Gates manual
// assignment and populates the work-item role pickers.
builder.Services.AddScoped<Platform.Api.Features.Settings.ParticipantRoleCatalog>();

// Agent
builder.Services.AddSingleton<A2UIFormGenerator>();
builder.Services.AddScoped<ValidationRunner>();
builder.Services.AddScoped<PlatformQueryService>();
builder.Services.AddHttpClient<CatalogAgent>();
builder.Services.AddScoped<CatalogAgent>();

// Webhooks
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>();
// The realtime decorator mirrors every dispatched event onto the SignalR hub so open pages
// refresh, then hands off to the real dispatcher for actual webhook delivery.
builder.Services.AddScoped<WebhookDispatcher>();
builder.Services.AddScoped<IWebhookDispatcher, RealtimeNotifyingWebhookDispatcher>();
builder.Services.AddHttpClient("webhook-delivery");
builder.Services.AddHostedService<WebhookDeliveryWorker>();

// Retry handler
builder.Services.AddScoped<RetryHandler>();

// Realtime — SignalR hub pushing entity-changed signals and chat notifications to the web app.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPlatformEventPublisher, SignalRPlatformEventPublisher>();

// Background services
builder.Services.AddHostedService<EscalationTimerService>();
var syncFromDisk = builder.Configuration.GetValue("Catalog:SyncFromDisk", builder.Environment.IsDevelopment());
if (syncFromDisk)
    builder.Services.AddHostedService<CatalogSyncService>();
builder.Services.AddHostedService<DeploymentEnrichmentService>();
builder.Services.AddHostedService<ExecutorWorkerService>();
// One-shot backfill of DeployEventWorkItem rows for existing deploy events.
// Idempotent and self-disabling once complete (records a flag in PlatformSettings).
builder.Services.AddHostedService<DeployEventWorkItemBackfillService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
        policy.WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();

        // In development, also accept any loopback origin whatever its port.
        //
        // Nothing should normally need this: the web dev server proxies /api and /agent through its own
        // origin, exactly as the production container's nginx does, so the browser makes no cross-origin
        // request at all. This is for the cases that bypass the proxy — a second dev server on another
        // port, a scratch page, curl with an Origin header — where the alternative is a bare CORS
        // failure that looks like the API being down.
        //
        // Loopback only, and only in Development: a deployed API still answers to the configured list.
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin =>
                origins.Contains(origin, StringComparer.OrdinalIgnoreCase) || IsLoopbackOrigin(origin));
        }
    });
});

// Local-only origin test for the development CORS relaxation above. Anything unparseable is refused
// rather than guessed at.
static bool IsLoopbackOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
    if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
    return System.Net.IPAddress.TryParse(uri.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
}

// Browsers cannot set an Authorization header on a WebSocket handshake, so the SignalR client
// sends the JWT as ?access_token=… instead. Accept it — for hub paths only, so ordinary API
// calls can't smuggle tokens through query strings (where they'd end up in access logs).
// Wraps any handler an auth library installed rather than replacing it.
static void AcceptHubAccessToken(JwtBearerOptions options)
{
    options.Events ??= new JwtBearerEvents();
    var prior = options.Events.OnMessageReceived;
    options.Events.OnMessageReceived = async context =>
    {
        if (prior is not null) await prior(context);

        string? accessToken = context.Request.Query["access_token"];
        if (string.IsNullOrEmpty(context.Token)
            && !string.IsNullOrEmpty(accessToken)
            && context.HttpContext.Request.Path.StartsWithSegments("/api/hubs"))
        {
            context.Token = accessToken;
        }
    };
}

// JSON serialization — handle circular references from EF navigation properties
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Apply pending EF Core migrations on every startup (idempotent).
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();

    // Seed feature flags so admins can flip them from the UI without touching appsettings.
    // Only inserts missing rows — never overwrites an operator's explicit value. In Development
    // every flag defaults on, so a fresh database shows the whole product instead of hiding
    // promotions, rollbacks and release notes behind a toggle.
    await FeatureFlagSeeder.SeedDefaults(
        db, builder.Configuration, enableAllByDefault: app.Environment.IsDevelopment());

    // One-time backfill of the built-in participant roles into an already-saved settings row, so an
    // install that predates a role its producers now send can still assign and filter on it.
    await ParticipantRoleSeeder.MergeDefaults(
        db, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ParticipantRoleSeeder)));

    // One-time migration of rollback enrollment (the retired rollback.enabledProducts setting) into
    // RollbackPolicy rows, preserving each enrolled product's existing approval gate. Creator lists
    // come out empty — admins only — because the old create path had no authorization to migrate from.
    await Platform.Api.Features.Rollbacks.RollbackPolicySeeder.MigrateEnrolledProducts(
        db, scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(Platform.Api.Features.Rollbacks.RollbackPolicySeeder)));

    // Seed catalog from YAML (production-safe: only adds new slugs)
    var loader = scope.ServiceProvider.GetRequiredService<CatalogYamlLoader>();
    await SeedData.SeedCatalog(db, loader);

    // Seed local users when MSAL is not configured (dev/test)
    if (!isMsal)
        await SeedData.SeedLocalUsers(db);

    // Seed demo data in development only.
    if (app.Environment.IsDevelopment())
    {
        await SeedData.SeedDemoData(db);
        await DeploymentSeedData.Seed(db);
        await PromotionSeedData.Seed(db);
    }
}

// Middleware pipeline
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();

// Expose OpenAPI spec (JSON only, no UI) in all environments.
app.MapOpenApi();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
    .AllowAnonymous();

// Auth config — tells the frontend which auth mode to use
app.MapGet("/api/auth/config", (IConfiguration config) =>
{
    return Results.Ok(new
    {
        mode = authMode.ToLowerInvariant(),
        clientId = isMsal ? config["AzureAd:ClientId"] ?? "" : "",
        tenantId = isMsal ? config["AzureAd:TenantId"] ?? "" : "",
    });
}).AllowAnonymous();

// Local auth endpoints (login/me) — only when MSAL is not configured
if (!isMsal)
    app.MapGroup("/api/auth").MapLocalAuthEndpoints();

// API endpoint groups — authorization policies always applied.
// In dev, local JWT satisfies the Bearer scheme; in prod, Entra ID does.
app.MapGroup("/api/catalog").MapCatalogEndpoints();
app.MapGroup("/api/catalog/admin").MapCatalogAdminEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
app.MapGroup("/api/requests").MapRequestEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/approvals").MapApprovalEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/audit").MapAuditEndpoints().RequireAuthorization(AuthorizationPolicies.AuditViewer);
app.MapGroup("/api/deployments").MapDeploymentEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/analytics").MapAnalyticsEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/deployments/admin").MapDeploymentAdminEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
app.MapGroup("/api/promotions").MapPromotionEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/promotions/admin").MapPromotionAdminEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
app.MapGroup("/api/rollbacks").MapRollbackEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/rollbacks/admin").MapRollbackAdminEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);
app.MapGroup("/api/work-items").MapWorkItemEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/release-notes").MapReleaseNoteEndpoints().RequireAuthorization(AuthorizationPolicies.CanApprove);
app.MapGroup("/api/settings").MapAppSettingsEndpoints().RequireAuthorization();
// The signed-in user's own preferences. Any authenticated user, no role gate — these are personal.
app.MapGroup("/api/me").MapUserPreferencesEndpoints().RequireAuthorization();
app.MapGroup("/api/features").MapFeatureFlagEndpoints();

// Webhooks — admin only (both schemes)
app.MapGroup("/api/webhooks").MapWebhookEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

app.MapGroup("/agent").MapAgentEndpoints().AllowAnonymous();

// Realtime hub — WebSocket (with SignalR's fallbacks) for entity-changed signals
app.MapHub<EventsHub>("/api/hubs/events").RequireAuthorization();

app.Run();

// Make the auto-generated Program class accessible to integration tests.
public partial class Program { }
