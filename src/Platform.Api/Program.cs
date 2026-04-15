using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Features.Approvals;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Deployments;
using Platform.Api.Features.Executors;
using Platform.Api.Features.Requests;
using Platform.Api.Infrastructure.Auth;
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
if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<SqlServerPlatformDbContext>(options =>
        options.UseSqlServer(dbConnectionString));
    builder.Services.AddScoped<PlatformDbContext>(sp => sp.GetRequiredService<SqlServerPlatformDbContext>());
}
else
{
    builder.Services.AddDbContext<PostgresPlatformDbContext>(options =>
        options.UseNpgsql(dbConnectionString));
    builder.Services.AddScoped<PlatformDbContext>(sp => sp.GetRequiredService<PostgresPlatformDbContext>());
}

// Auth — mode is explicitly configured: "Msal" (Azure AD) or "Local" (DB-based JWT)
var authMode = (builder.Configuration["Auth:Mode"] ?? "Local").Trim();
if (authMode.Equals("Msal", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
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

// Agent
builder.Services.AddSingleton<A2UIFormGenerator>();
builder.Services.AddScoped<ValidationRunner>();
builder.Services.AddScoped<PlatformQueryService>();
builder.Services.AddHttpClient<CatalogAgent>();
builder.Services.AddScoped<CatalogAgent>();

// Webhooks
builder.Services.AddDataProtection();
builder.Services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
builder.Services.AddHttpClient("webhook-delivery");
builder.Services.AddHostedService<WebhookDeliveryWorker>();

// Retry handler
builder.Services.AddScoped<RetryHandler>();

// SSE / Realtime
builder.Services.AddSingleton<SseConnectionManager>();
builder.Services.AddScoped<IPlatformEventPublisher, SsePlatformEventPublisher>();

// Background services
builder.Services.AddHostedService<EscalationTimerService>();
var syncFromDisk = builder.Configuration.GetValue("Catalog:SyncFromDisk", builder.Environment.IsDevelopment());
if (syncFromDisk)
    builder.Services.AddHostedService<CatalogSyncService>();
builder.Services.AddHostedService<DeploymentEnrichmentService>();
builder.Services.AddHostedService<ExecutorWorkerService>();

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
    });
});

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
    // Only inserts missing rows — never overwrites an operator's explicit value.
    await FeatureFlagSeeder.SeedDefaults(db, builder.Configuration);

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
app.MapGroup("/api/deployments/admin").MapDeploymentAdminEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

// Webhooks — admin only (both schemes)
app.MapGroup("/api/webhooks").MapWebhookEndpoints().RequireAuthorization(AuthorizationPolicies.CatalogAdmin);

app.MapGroup("/agent").MapAgentEndpoints().AllowAnonymous();

// SSE endpoint
app.MapSseEndpoints();

app.Run();
