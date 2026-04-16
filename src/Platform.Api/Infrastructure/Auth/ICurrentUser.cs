namespace Platform.Api.Infrastructure.Auth;

public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }
    string Email { get; }
    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAdmin { get; }
    /// <summary>
    /// True when the user holds <c>InfraPortal.QA</c> — a lightweight promotion-capable role
    /// for small teams that don't need AD security groups.
    /// </summary>
    bool IsQA { get; }
    bool IsInGroup(string groupId);
}
