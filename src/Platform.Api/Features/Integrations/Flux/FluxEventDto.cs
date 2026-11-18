namespace Platform.Api.Features.Integrations.Flux;

/// <summary>
/// The event Flux's notification-controller posts to a <c>generic</c> / <c>generic-hmac</c>
/// provider. Only the fields the adapter reads are bound; the rest of the payload is ignored.
/// <c>Metadata</c> holds the event's own metadata (the chart <c>revision</c>), the Alert's
/// <c>eventMetadata</c> (<c>environment</c>, <c>cluster</c>) and the object's
/// <c>event.toolkit.fluxcd.io/*</c> annotations (<c>product</c>, <c>service</c>,
/// <c>app-version</c>), all as strings.
/// </summary>
public record FluxEventDto(
    FluxInvolvedObject? InvolvedObject,
    string? Severity,
    DateTimeOffset? Timestamp,
    string? Message,
    string? Reason,
    string? ReportingController,
    Dictionary<string, string>? Metadata);

public record FluxInvolvedObject(string? Kind, string? Namespace, string? Name);
