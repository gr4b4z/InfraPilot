using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Api.BackgroundServices;
using Platform.Api.Features.ServiceInventory.Http;
using Platform.Api.Features.ServiceInventory.Sources;

namespace Platform.Api.Features.ServiceInventory;

public static class ServiceInventoryRegistration
{
    public static IServiceCollection AddServiceInventory(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ServiceInventoryOptions>(configuration.GetSection(ServiceInventoryOptions.SectionName));
        services.AddHttpClient(InventoryHttpClient.ClientName)
            // Redirects are followed by InventoryHttpClient, which re-checks every hop and drops
            // credentials off their host; the handler must never follow one on its own.
            // No cookies either: a cookie set by one host could otherwise reach a sibling host and
            // bypass the per-host credential rule.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            // The default request logging writes the URL, which may carry a SAS token.
            .RemoveAllLoggers();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OciTokenCache>();
        services.AddScoped<InventoryHttpClient>();
        services.AddScoped<IArtifactSourceFactory, ArtifactSourceFactory>();
        services.AddScoped<InventoryRefresher>();
        services.AddScoped<InventoryRetention>();
        services.AddScoped<InventoryReadService>();
        services.AddHostedService<ServiceInventorySyncService>();
        return services;
    }
}
