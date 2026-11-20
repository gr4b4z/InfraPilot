using Microsoft.Extensions.Options;
using Platform.Api.Features.ServiceInventory.Http;

namespace Platform.Api.Features.ServiceInventory.Sources;

public interface IArtifactSourceFactory
{
    IArtifactSource Create(InventoryFeedOptions feed);
}

/// <summary>
/// Turns a configured feed into its source. A new kind of place is one more case here and one
/// class; a new feed or a moved feed is configuration only.
/// </summary>
public class ArtifactSourceFactory : IArtifactSourceFactory
{
    private readonly InventoryHttpClient _http;
    private readonly OciTokenCache _tokens;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<ServiceInventoryOptions> _options;

    public ArtifactSourceFactory(
        InventoryHttpClient http, OciTokenCache tokens, TimeProvider clock, IOptionsMonitor<ServiceInventoryOptions> options)
    {
        _http = http;
        _tokens = tokens;
        _clock = clock;
        _options = options;
    }

    public IArtifactSource Create(InventoryFeedOptions feed)
    {
        var options = _options.CurrentValue;
        InventoryCredentialOptions? credential = null;
        if (!string.IsNullOrWhiteSpace(feed.Credential)) options.Credentials.TryGetValue(feed.Credential, out credential);

        var source = feed.Source;
        return source.Kind.Trim().ToLowerInvariant() switch
        {
            InventorySourceKinds.Oci => new OciArtifactSource(_http, _tokens, _clock,
                source.Registry!.Trim(), source.Repository!.Trim().Trim('/'), source.Tag.Trim(), source.File!.Trim(), credential,
                source.AuthHost),
            InventorySourceKinds.Https => new HttpsArtifactSource(_http, new Uri(source.Url!),
                source.ClosedMonths?.UrlTemplate, credential),
            _ => throw new InvalidOperationException($"Unknown source kind '{source.Kind}'."),
        };
    }
}
