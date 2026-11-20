using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Platform.Api.Agent;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Features.Settings;
using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure.Auth;
using Platform.Api.Infrastructure.Identity;

namespace Platform.Api.Mcp;

/// <summary>
/// The participant-role vocabulary (Settings → Participant Roles) and the directory behind its pickers
/// — what an agent needs to keep a role's team current: look the people up, read the role, change it.
/// The write goes through <see cref="AppSettingsService.UpdateRoleAsync"/>, the same path as
/// <c>PATCH /api/settings/roles/{key}</c>, so it touches that one role and nothing else.
/// </summary>
[McpServerToolType]
public sealed class SettingsTools(
    McpContext ctx, AppSettingsService settings, ParticipantRoleCatalog roleCatalog,
    IIdentityService identity, ICurrentUser user, ILogger<SettingsTools> log)
{
    private const int DefaultPeopleLimit = 10;

    [McpServerTool(Name = "list_participant_roles", Title = "List participant roles", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The participant roles configured in the portal (Settings → Participant Roles): each role's key and label, " +
                 "its aliases (other names producers send for it, such as \"qa\" for \"qa-owner\"), the directory groups its " +
                 "assignment picker is limited to, and its team — the people usually given the role, offered first wherever " +
                 "someone is assigned to it and always listed in the work-items queue's Assigned to filter.")]
    public async Task<string> ListParticipantRoles(CancellationToken ct = default)
    {
        var current = await settings.GetSettings(ct);
        return ctx.Result(new
        {
            roles = (current.Roles ?? []).Select(Shape),
            url = ctx.Url(PortalRoutes.ParticipantRoles()),
        });
    }

    [McpServerTool(Name = "update_participant_role", Title = "Change a participant role", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Admin only. Changes one configured participant role and leaves every other setting as it is. Each field you " +
                 "pass replaces what the role has; leave a field out to keep it, or pass an empty list to clear it. `team` is the " +
                 "whole team, so to add one person pass the current team (list_participant_roles) plus them. Get exact emails " +
                 "with find_people first. Only changes an existing role; an unknown role fails with the configured list.")]
    public async Task<string> UpdateParticipantRole(
        [Description("Role key or one of its aliases, e.g. \"qa-owner\".")] string role,
        [Description("New label shown in the portal.")] string? displayName = null,
        [Description("Every other name producers send for this role (replaces the list).")] string[]? aliases = null,
        [Description("Directory groups the role's picker is limited to, as { id, name } (replaces the list). Empty means anyone.")] GroupRef[]? assigneeGroups = null,
        [Description("The people who usually take this role, as { email, displayName } (replaces the whole team).")] RoleTeamMemberDto[]? team = null,
        CancellationToken ct = default)
    {
        if (!user.IsAdmin)
            throw new McpException("Only an admin can change participant roles. Ask an admin, or use an Admin personal token.");
        if (displayName is null && aliases is null && assigneeGroups is null && team is null)
            throw new McpException("Nothing to change: pass at least one of displayName, aliases, assigneeGroups or team.");

        var result = await settings.UpdateRoleAsync(role, new RoleConfigPatchDto(
            displayName, aliases?.ToList(), assigneeGroups?.ToList(), team?.ToList()), ct);
        if (result.NotFound is { } missing) throw new McpException(missing);
        if (result.Errors.Count > 0) throw new McpException(string.Join(" ", result.Errors));

        return ctx.Result(new { role = Shape(result.Role!), url = ctx.Url(PortalRoutes.ParticipantRoles()) });
    }

    [McpServerTool(Name = "find_people", Title = "Find people in the directory", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the organisation's directory by name or email (at least 2 characters) — the search behind the portal's " +
                 "person pickers. Pass `role` to search only the people that role's picker offers (its assignee groups). Use it " +
                 "to get someone's exact email before assigning them or adding them to a role's team; spellings in chat are " +
                 "often not the directory's.")]
    public async Task<string> FindPeople(
        [Description("Part of a name or email, e.g. \"Polgar\" or \"balint.p\".")] string query,
        [Description("Role key; limits the search to the groups configured for it.")] string? role = null,
        [Description("Maximum people (1-50). Default 10.")] int? limit = null,
        CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) throw new McpException("Give at least 2 characters of a name or email.");
        var cap = Math.Clamp(limit ?? DefaultPeopleLimit, 1, 50);
        IReadOnlyList<GroupRef> groups = string.IsNullOrWhiteSpace(role)
            ? []
            : await roleCatalog.GetAssigneeGroupsAsync(role, ct);

        IReadOnlyList<UserInfo> people;
        try
        {
            people = groups.Count == 0
                ? await identity.SearchUsers(q, ct)
                : await identity.SearchUsersInGroups(q, groups.Select(g => new GroupInfo(g.Id, g.Name)).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The REST search swallows this into an empty list for the picker; an agent is better
            // told, so it doesn't read "nobody matched" and give up on a real person.
            log.LogWarning(ex, "Directory search failed for an MCP caller via {Provider}", identity.GetType().Name);
            throw new McpException("The directory can't be searched right now. Try again later, or ask the user for the exact email.");
        }

        return ctx.Result(new
        {
            people = people.Take(cap).Select(p => new { name = p.DisplayName, email = p.Email }),
            more = people.Count > cap ? people.Count - cap : (int?)null,
            restrictedTo = groups.Count == 0 ? null : groups.Select(g => g.Name).ToList(),
        });
    }

    private static object Shape(RoleConfigDto r) => new
    {
        key = r.Key,
        displayName = r.DisplayName,
        aliases = r.Aliases is { Count: > 0 } a ? a : null,
        assigneeGroups = r.AssigneeGroups is { Count: > 0 } g ? g.Select(x => new { id = x.Id, name = x.Name }) : null,
        team = r.Team is { Count: > 0 } t ? t.Select(m => new { email = m.Email, displayName = m.DisplayName }) : null,
    };
}
