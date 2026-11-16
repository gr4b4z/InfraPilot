using Platform.Api.Infrastructure.Auth;

namespace Platform.Api.Features.Users;

/// <summary>
/// Who the signed-in user is, beyond what their token says, under <c>/api/me</c>.
/// </summary>
public static class CurrentUserEndpoints
{
    public static RouteGroupBuilder MapCurrentUserEndpoints(this RouteGroupBuilder group)
    {
        // Every address that is the caller: the sign-in name first, then the directory's others (see
        // DirectoryEmailClaimsTransformation). The web app reads the sign-in name from the token, but a
        // work item names its people by their mail, so "which of these people is me?" needs the whole
        // list. One call at app bootstrap.
        group.MapGet("/emails", (ICurrentUser user) => Results.Ok(new
        {
            email = user.Email,
            emails = user.Emails,
        }));

        return group;
    }
}
