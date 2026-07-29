using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// The platform's participant-role vocabulary: the roles an operator has configured under
/// Settings → Participant Roles (the <c>Roles</c> list of <see cref="AppSettingsService"/>'s
/// <c>ui.app-settings</c> row). Keys are canonicalised with <see cref="RoleNormalizer"/> so a
/// lookup never depends on how the admin typed them.
///
/// <para>Two jobs:</para>
/// <list type="bullet">
///   <item><b>Gate manual assignment.</b> A person can only be put on a role the platform knows
///         about — otherwise every typo becomes a permanent, unfilterable slot on the work item.
///         Enforced at the API surface (see <c>PromotionEndpoints</c>); the ingest path is
///         deliberately exempt, since a producer's payload is a fact to record, not a request to
///         validate. Roles that arrive that way get surfaced as unrecognised instead.</item>
///   <item><b>Populate the pickers.</b> The work-items queue's role filter and the assign
///         popover both list this set, so the choices on offer are always the current
///         configuration rather than whatever happens to be present in the data.</item>
/// </list>
///
/// <para>An empty configured list means an empty vocabulary — nothing can be manually assigned
/// and every incoming role reads as unrecognised. That only happens when an admin explicitly
/// saves an empty list: a fresh install with no settings row at all still gets
/// <see cref="AppSettingsService.Defaults"/>. Deliberately no fallback here, so the server and
/// the web client (which reads the same list from its settings store) can never disagree about
/// which roles exist.</para>
/// </summary>
public class ParticipantRoleCatalog
{
    private readonly AppSettingsService _settings;

    public ParticipantRoleCatalog(AppSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// The configured role keys, canonicalised and deduped, in the order the admin arranged them
    /// (which is the order the UI's pickers render).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCanonicalKeysAsync(CancellationToken ct = default)
    {
        var settings = await _settings.GetSettings(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var role in settings.Roles ?? [])
        {
            var canonical = RoleNormalizer.Normalize(role.Key);
            if (canonical.Length == 0 || !seen.Add(canonical)) continue;
            keys.Add(canonical);
        }
        return keys;
    }

    /// <summary>Set form of <see cref="GetCanonicalKeysAsync"/>, for membership tests.</summary>
    public async Task<HashSet<string>> GetCanonicalSetAsync(CancellationToken ct = default)
        => new(await GetCanonicalKeysAsync(ct), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="role"/> canonicalises onto a configured participant role. Blank
    /// input is never configured.
    /// </summary>
    public async Task<bool> IsConfiguredAsync(string? role, CancellationToken ct = default)
    {
        var canonical = RoleNormalizer.Normalize(role);
        if (canonical.Length == 0) return false;
        var configured = await GetCanonicalSetAsync(ct);
        return configured.Contains(canonical);
    }

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
