using Platform.Api.Features.Settings.Models;
using Platform.Api.Infrastructure;

namespace Platform.Api.Features.Settings;

/// <summary>
/// The scoped front door to <see cref="EnvironmentAliasMap"/>: loads the configured environments
/// once per request and answers every environment resolution from that snapshot.
///
/// <para><b>One resolution point, not fifteen.</b> Environment strings enter from a dozen places —
/// deploy ingest, manual deploys, external promotion create, promotion-from-build, rollback
/// create/preview, release-note generation, webhook filters, the policy editors — and every one of
/// them has to agree on which environment "prod" means, or a deploy lands on <c>prod</c> while the
/// promotion waiting for it is still keyed to <c>production</c>. The same map also resolves the
/// environment on read filters, so a pipeline that queries by its own name for the environment gets
/// the rows it just wrote.</para>
///
/// <para>Memoised for the request, like <c>ServiceProductOverrideService</c>: an ingest resolves
/// several times (payload, replay key, previous-version lookup) and a settings change is picked up
/// by the next request.</para>
/// </summary>
public class EnvironmentAliasResolver
{
    private readonly AppSettingsService _settings;
    private readonly ILogger<EnvironmentAliasResolver> _logger;
    private List<EnvironmentConfigDto>? _configured;
    private EnvironmentAliasMap? _memo;
    private IReadOnlySet<string>? _retired;

    public EnvironmentAliasResolver(AppSettingsService settings, ILogger<EnvironmentAliasResolver> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    // One settings read per request, shared by the alias map and the retired set: a state request
    // resolves its environment filter and then drops retired environments, and the two must agree.
    private async Task<List<EnvironmentConfigDto>> ConfiguredAsync(CancellationToken ct)
        => _configured ??= (await _settings.GetSettings(ct)).Environments ?? [];

    /// <summary>The configured map for this request.</summary>
    public async Task<EnvironmentAliasMap> MapAsync(CancellationToken ct = default)
        => _memo ??= EnvironmentAliasMap.Build(await ConfiguredAsync(ct));

    /// <summary>
    /// The environments the current-state read paths leave out, for this request — see
    /// <see cref="AppSettingsService.RetiredEnvironmentKeys"/>. Lives here rather than only on
    /// <see cref="AppSettingsService"/> because the read paths that need it already hold this
    /// resolver, and answering from its snapshot keeps them at one settings read.
    /// </summary>
    public async Task<IReadOnlySet<string>> RetiredKeysAsync(CancellationToken ct = default)
        => _retired ??= AppSettingsService.RetiredEnvironmentKeys(await ConfiguredAsync(ct));

    /// <summary>
    /// The canonical key to store for an environment a caller supplied. Passes through unchanged
    /// when no environment is configured for it.
    /// </summary>
    public async Task<string> ResolveAsync(string? sent, CancellationToken ct = default)
    {
        var match = (await MapAsync(ct)).Match(sent);
        if (match.Aliased)
        {
            _logger.LogInformation(
                "Environment alias: '{Sent}' resolved to '{Environment}'",
                LogSanitizer.Clean(match.Sent), LogSanitizer.Clean(match.Key));
        }
        return match.Key;
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> for a query filter: null in, null out, so "all environments"
    /// survives the round trip.
    /// </summary>
    public async Task<string?> ResolveFilterAsync(string? sent, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(sent) ? null : await ResolveAsync(sent, ct);
}
