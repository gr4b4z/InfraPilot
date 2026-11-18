using Platform.Api.Features.Integrations.Flux;

namespace Platform.Api.Features.Integrations;

/// <summary>
/// <c>/api/integrations</c> — adapters for systems that report in their own format. Each one maps its
/// payload onto the core model and calls the core services; the canonical APIs
/// (<c>/api/deployments/events</c>, <c>/api/promotions</c>) stay the only documented shapes.
/// Integrations are called by machines, so the group has no user policy; each adapter brings its own.
/// </summary>
public static class IntegrationEndpoints
{
    public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder group, IConfiguration config)
    {
        // Mapped only when enabled, so a portal that never configured Flux answers 404 rather than 401.
        var flux = config.GetSection(FluxIntegrationOptions.SectionName).Get<FluxIntegrationOptions>();
        if (flux?.Enabled == true)
            group.MapGroup("/flux").MapFluxEndpoints();

        return group;
    }
}
