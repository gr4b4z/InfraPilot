namespace Platform.Api.Infrastructure;

/// <summary>
/// Sanitises externally-supplied strings before they go into log messages. Strips CR/LF so a
/// caller-controlled value (deploy-event product/service/version, etc.) can't forge additional
/// log lines (log injection / forging), and bounds the length to keep entries readable.
/// </summary>
public static class LogSanitizer
{
    public static string Clean(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = value.Replace("\r", "").Replace("\n", "");
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
