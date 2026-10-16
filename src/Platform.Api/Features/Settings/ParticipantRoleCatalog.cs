using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// The platform's participant-role vocabulary: the roles an operator has configured under
/// Settings → Participant Roles (the <c>Roles</c> list of <see cref="AppSettingsService"/>'s
/// <c>ui.app-settings</c> row). Keys are canonicalised with <see cref="RoleNormalizer"/> so a
/// lookup never depends on how the admin typed them, and each role's aliases resolve onto it (see
/// <see cref="RoleAliasMap"/>).
///
/// <para>Three jobs:</para>
/// <list type="bullet">
///   <item><b>Gate manual assignment.</b> A person can only be put on a role the platform knows
///         about — otherwise every typo becomes a permanent, unfilterable slot on the work item.
///         Enforced at the API surface (see <c>PromotionEndpoints</c>); the ingest path is
///         deliberately exempt, since a producer's payload is a fact to record, not a request to
///         validate. Roles that arrive that way get surfaced as unrecognised instead. An alias of a
///         configured role counts as configured — it is that role.</item>
///   <item><b>Populate the pickers.</b> The assign popover lists this set, so the choices on
///         offer are always the current configuration rather than whatever happens to be
///         present in the data — and the person search narrows to a role's assignee groups.</item>
///   <item><b>Resolve aliases.</b> <see cref="MapAsync"/> is the request-scoped
///         <see cref="RoleAliasMap"/> every role comparison on the read paths goes through.</item>
/// </list>
///
/// <para>An empty configured list means an empty vocabulary — nothing can be manually assigned
/// and every incoming role reads as unrecognised. That only happens when an admin explicitly
/// saves an empty list: a fresh install with no settings row at all still gets
/// <see cref="AppSettingsService.Defaults"/>. Deliberately no fallback here, so the server and
/// the web client (which reads the same list from its settings store) can never disagree about
/// which roles exist.</para>
///
/// <para>Memoised for the request, like <see cref="EnvironmentAliasResolver"/>: the promotions list
/// resolves roles for every row, and a settings change is picked up by the next request.</para>
/// </summary>
public class ParticipantRoleCatalog
{
    private readonly AppSettingsService _settings;
    private RoleAliasMap? _memo;

    public ParticipantRoleCatalog(AppSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>The configured vocabulary for this request, aliases and assignee groups included.</summary>
    public async Task<RoleAliasMap> MapAsync(CancellationToken ct = default)
        => _memo ??= RoleAliasMap.Build((await _settings.GetSettings(ct)).Roles);

    /// <summary>
    /// The configured role keys, canonicalised and deduped, in the order the admin arranged them
    /// (which is the order the UI's pickers render). Aliases are not keys and are not listed.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCanonicalKeysAsync(CancellationToken ct = default)
        => (await MapAsync(ct)).Keys;

    /// <summary>Set form of <see cref="GetCanonicalKeysAsync"/>, for membership tests.</summary>
    public async Task<HashSet<string>> GetCanonicalSetAsync(CancellationToken ct = default)
        => new(await GetCanonicalKeysAsync(ct), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="role"/> canonicalises onto a configured participant role, directly or
    /// as one of its aliases. Blank input is never configured.
    /// </summary>
    public async Task<bool> IsConfiguredAsync(string? role, CancellationToken ct = default)
        => (await MapAsync(ct)).IsConfigured(role);

    /// <summary>
    /// The groups the person picker narrows <paramref name="role"/> to (any of them), after alias
    /// resolution. Empty means no restriction.
    /// </summary>
    public async Task<IReadOnlyList<GroupRef>> GetAssigneeGroupsAsync(string? role, CancellationToken ct = default)
        => (await MapAsync(ct)).AssigneeGroups(role);

    /// <summary>
    /// The 400-response wording for an unconfigured role. Names the configured set so the caller
    /// can see what it should have sent (or what to go add).
    /// </summary>
    public static string RejectionMessage(string? role, IReadOnlyList<string> configured)
    {
        var canonical = RoleNormalizer.Normalize(role);
        var known = configured.Count == 0
            ? "none are configured yet"
            : string.Join(", ", configured);
        return $"'{canonical}' is not a configured participant role ({known}). "
             + "Add it under Settings → Participant Roles before assigning anyone to it.";
    }
}
