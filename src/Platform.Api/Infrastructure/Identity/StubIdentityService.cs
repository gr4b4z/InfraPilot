using Microsoft.EntityFrameworkCore;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Api.Infrastructure.Identity;

public class StubIdentityService : IIdentityService
{
    private readonly PlatformDbContext _db;

    public StubIdentityService(PlatformDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<UserInfo>> GetGroupMembers(string groupId, CancellationToken ct = default)
    {
        var users = await _db.LocalUsers
            .Where(u => u.IsActive)
            .Select(u => new UserInfo(u.Id.ToString(), u.Name, u.Email))
            .ToListAsync(ct);
        return users.Count > 0
            ? users
            : [new UserInfo("user-1", "Dev User", "dev@localhost")];
    }

    public async Task<UserInfo?> GetUser(string userId, CancellationToken ct = default)
    {
        // Try parsing as Guid (local user ID) first
        if (Guid.TryParse(userId, out var guid))
        {
            var localUser = await _db.LocalUsers.FirstOrDefaultAsync(u => u.Id == guid, ct);
            if (localUser is not null)
                return new UserInfo(localUser.Id.ToString(), localUser.Name, localUser.Email);
        }

        // Fallback — return a stub so callers never get null for known IDs
        return new UserInfo(userId, "Dev User", "dev@localhost");
    }

    public async Task<IReadOnlyList<UserInfo>> SearchUsers(string query, CancellationToken ct = default)
    {
        var users = await _db.LocalUsers
            .Where(u => u.IsActive && (u.Name.Contains(query) || u.Email.Contains(query)))
            .Select(u => new UserInfo(u.Id.ToString(), u.Name, u.Email))
            .ToListAsync(ct);
        return users.Count > 0
            ? users
            : [new UserInfo("user-1", "Dev User", "dev@localhost")];
    }

    // Static dev groups so the policy editor's group picker is usable under local auth. In local
    // mode the approval-time check matches groups by name against the user's Roles claim, so Id and
    // DisplayName are intentionally the same value here.
    private static readonly string[] DevGroups =
    [
        "InfraPortal.Admin",
        "InfraPortal.QA",
        "InfraPortal.Reviewer",
        "SWO-PLT-TeamLeads",
        "SWO-PLT-Engineers",
    ];

    public Task<IReadOnlyList<GroupInfo>> SearchGroups(string query, CancellationToken ct = default)
    {
        var matches = DevGroups
            .Where(g => g.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(g => new GroupInfo(g, g))
            .ToList();
        IReadOnlyList<GroupInfo> result = matches.Count > 0
            ? matches
            : DevGroups.Select(g => new GroupInfo(g, g)).ToList();
        return Task.FromResult(result);
    }
}
