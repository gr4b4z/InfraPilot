namespace Platform.Api.Infrastructure.Auth;

public interface ICurrentUser
{
    string Id { get; }
    string Name { get; }
    string Email { get; }
    IReadOnlyList<string> Groups { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAdmin { get; }
    bool IsInGroup(string groupId);
}
