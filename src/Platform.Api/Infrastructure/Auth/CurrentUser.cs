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
    // Entra emits the signed-in identity under different claims depending on the token version:
    // v2.0 tokens carry "preferred_username", v1.0 tokens carry "upn" / "unique_name" (and NO
    // "preferred_username" or "email"). Because MapInboundClaims is disabled, claims keep their raw
    // JWT names, so ClaimTypes.Email never matches an Entra token. Probe all of them (v2 first, then
    // v1, then the ws-fed-mapped fallbacks) so Email is never empty for a real signed-in user —
    // an empty Email silently breaks approver Users-list matching and approval de-duplication.
    public string Email =>
        FirstNonBlank(
            User?.FindFirstValue("preferred_username"),
            User?.FindFirstValue("upn"),
            User?.FindFirstValue("email"),
            User?.FindFirstValue("unique_name"),
            User?.FindFirstValue(ClaimTypes.Email),
            User?.FindFirstValue(ClaimTypes.Upn))
        ?? "";

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public IReadOnlyList<string> Groups =>
        User?.FindAll("groups").Select(c => c.Value).ToList().AsReadOnly()
        ?? new List<string>().AsReadOnly();

    public IReadOnlyList<string> Roles =>
        User?.FindAll("roles").Select(c => c.Value).ToList().AsReadOnly()
        ?? new List<string>().AsReadOnly();

    public bool IsAdmin => Roles.Contains("InfraPortal.Admin", StringComparer.OrdinalIgnoreCase);
    public bool IsQA => Roles.Contains("InfraPortal.QA", StringComparer.OrdinalIgnoreCase);

    public bool IsInGroup(string groupId) => Groups.Contains(groupId, StringComparer.OrdinalIgnoreCase);
}
