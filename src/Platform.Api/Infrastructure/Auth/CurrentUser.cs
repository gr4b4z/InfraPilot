using System.Security.Claims;

namespace Platform.Api.Infrastructure.Auth;

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public string Id => User?.FindFirstValue("oid") ?? User?.FindFirstValue("sub") ?? User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
    public string Name => User?.FindFirstValue("name") ?? User?.FindFirstValue(ClaimTypes.Name) ?? "Anonymous";
    public string Email => User?.FindFirstValue("preferred_username") ?? User?.FindFirstValue(ClaimTypes.Email) ?? "";

    public IReadOnlyList<string> Groups =>
        User?.FindAll("groups").Select(c => c.Value).ToList().AsReadOnly()
        ?? new List<string>().AsReadOnly();

    public IReadOnlyList<string> Roles =>
        User?.FindAll("roles").Select(c => c.Value).ToList().AsReadOnly()
        ?? new List<string>().AsReadOnly();

    public bool IsAdmin => Roles.Contains("InfraPortal.Admin");

    public bool IsInGroup(string groupId) => Groups.Contains(groupId);
}
