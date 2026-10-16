namespace Platform.Api.Infrastructure.Auth;

public static class PersonalTokenRestrictions
{
    /// <summary>
    /// Refuses the route to a personal access token (403), for decisions a person makes themselves:
    /// approving, rejecting, signing off. A token is how an agent acts for someone, and these are the
    /// actions an agent must not take on the strength of whatever it was told — a prompt it read in a
    /// ticket description is enough to make it try. The person makes them in the portal.
    /// </summary>
    public static TBuilder DenyPersonalTokens<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
            PersonalAccessTokenService.IsPersonalToken(context.HttpContext.User)
                ? Results.Json(
                    new { error = "Personal access tokens cannot approve, reject or sign off. Make this decision in the portal." },
                    statusCode: StatusCodes.Status403Forbidden)
                : await next(context));
}
