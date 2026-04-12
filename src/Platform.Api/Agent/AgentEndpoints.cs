namespace Platform.Api.Agent;

public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/catalog/chat", async (CatalogAgent agent, CatalogAgentRequest request) =>
        {
            var response = await agent.HandleAsync(request);
            return Results.Ok(response);
        });

        return group;
    }
}
