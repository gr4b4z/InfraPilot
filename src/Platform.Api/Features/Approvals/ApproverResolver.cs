using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Features.Approvals;

public class ApproverResolver
{
    private readonly IIdentityService _identityService;

    public ApproverResolver(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public async Task<IReadOnlyList<UserInfo>> ResolveApprovers(string groupId, CancellationToken ct = default)
    {
        return await _identityService.GetGroupMembers(groupId, ct);
    }
}
