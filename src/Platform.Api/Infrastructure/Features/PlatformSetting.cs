namespace Platform.Api.Infrastructure.Features;

/// <summary>
/// Generic key/value settings row backing server-side-authoritative platform config
/// (feature flags, promotion topology, etc). Keep values small and JSON-serializable.
/// </summary>
public class PlatformSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedBy { get; set; } = "system";
}
