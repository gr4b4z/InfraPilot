namespace Platform.Api.Infrastructure.Logging;

/// <summary>
/// Strips characters that could enable log-injection attacks (newlines, control chars)
/// from user-provided values before they reach structured-logging parameters.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with CR, LF, and ASCII control characters replaced by underscores.
    /// Null/empty inputs pass through unchanged.
    /// </summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Fast path: most values have no control chars.
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
                return SanitizeSlow(value);
        }

        return value;
    }

    private static string SanitizeSlow(string value)
    {
        var buf = value.ToCharArray();
        for (var i = 0; i < buf.Length; i++)
        {
            if (char.IsControl(buf[i]))
                buf[i] = '_';
        }
        return new string(buf);
    }
}
