namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// <c>Integrations:Flux</c> — the Flux adapter (<c>POST /api/integrations/flux/{client}/events</c>).
/// Each cluster that reports to the portal is a named <see cref="FluxClient"/> with its own HMAC
/// secret: Flux's <c>generic-hmac</c> provider signs the request body with it, and the signature
/// header does not say who signed, so the client is named in the URL.
/// </summary>
public class FluxIntegrationOptions
{
    public const string SectionName = "Integrations:Flux";

    /// <summary>
    /// Maps the endpoint at startup. Off by default: an ingest path nobody configured stays closed,
    /// and turning it on is a restart, like the other ingest settings.
    /// </summary>
    public bool Enabled { get; set; }

    public List<FluxClient> Clients { get; set; } = [];
}

public class FluxClient
{
    /// <summary>The <c>{client}</c> segment of the URL, usually the cluster name.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The shared secret, the same value as the <c>token</c> key of the Flux provider secret. Kept
    /// in clear (from the secret store), not as a hash: an HMAC can only be checked with the key.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>Products this client may report for. Empty means all, as for API keys.</summary>
    public List<string> AllowedProducts { get; set; } = [];

    /// <summary>A client can be switched off without removing it.</summary>
    public bool Revoked { get; set; }
}
