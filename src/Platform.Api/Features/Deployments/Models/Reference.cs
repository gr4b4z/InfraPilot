namespace Platform.Api.Features.Deployments.Models;

public record Reference(
    string Type,
    string? Url = null,
    string? Provider = null,
    string? Key = null,
    string? Revision = null);
