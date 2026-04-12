namespace Platform.Api.Features.Deployments.Models;

public record Participant(
    string Role,
    string? DisplayName = null,
    string? Email = null);
