using System.Security.Claims;
using Platform.Api.Infrastructure.Audit;

namespace Platform.Api.Infrastructure.Auth;

public static class PersonalTokenRestrictions
{
    /// <summary>
    /// Refuses the route (403) to a personal access token below <paramref name="minimum"/>, for
    /// decisions a person makes: approving, rejecting, signing off. A token is how an agent acts for
    /// someone, and an agent can be talked into a decision by a prompt it read in a ticket
    /// description, so a standard token makes none. A QA or Admin token is one its owner created to
    /// delegate exactly these (see <see cref="PersonalTokenAccess"/>); the route's own checks then
    /// apply to it as to the owner. Configured keys and signed-in sessions are not affected.
    ///
    /// <para>A decision a token gets through is audited as <c>personal_token.decision</c> — the
    /// decision's own record names the owner, and this entry is what says an agent made it.</para>
    /// </summary>
    public static TBuilder RequirePersonalTokenAccess<TBuilder>(this TBuilder builder, PersonalTokenAccess minimum)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var access = PersonalAccessTokenService.AccessOf(http.User);
            if (access is null) return await next(context);
            if (access < minimum)
                return Results.Json(new { error = Refusal(access.Value, minimum) }, statusCode: StatusCodes.Status403Forbidden);

            var result = await next(context);
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            if (status is >= 200 and < 300)
                await AuditDecisionAsync(http, access.Value, status);
            return result;
        });

    private static string Refusal(PersonalTokenAccess access, PersonalTokenAccess minimum) =>
        (minimum == PersonalTokenAccess.QA
            ? "Work-item sign-off needs a QA or Admin personal access token"
            : "This decision needs an Admin personal access token")
        + $"; this one is {access}. Make this decision in the portal, or have an agent make it with such a token from the Agent access page.";

    private static async Task AuditDecisionAsync(HttpContext http, PersonalTokenAccess access, int status)
    {
        var user = http.User;
        var tokenId = Guid.TryParse(user.FindFirstValue(PersonalAccessTokenService.TokenIdClaim), out var id) ? id : (Guid?)null;
        var audit = http.RequestServices.GetRequiredService<IAuditLogger>();
        await audit.Log("auth", "personal_token.decision",
            user.FindFirstValue("oid") ?? "", user.FindFirstValue("name") ?? "", "user",
            nameof(PersonalAccessToken), tokenId, null, null,
            new { method = http.Request.Method, path = http.Request.Path.Value, status, access = access.ToString() });
    }
}
