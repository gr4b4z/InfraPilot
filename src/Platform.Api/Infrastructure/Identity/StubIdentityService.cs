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
}
