namespace Platform.Api.Infrastructure;

/// <summary>
/// Opt-in canonicalisation for role and environment strings received from external senders
/// (primarily the deployment ingest webhook and the promotion participant-assign endpoint).
/// <para>
/// Each field holds the target casing. When <c>null</c> or empty, values are stored exactly
/// as sent; when set to <c>"kebab-case"</c> the <see cref="RoleNormalizer"/> is applied
/// (lowercase, kebab-case, camelCase boundary split). Unknown values are treated as <c>null</c>.
/// </para>
/// <para>
/// Keep the vocabulary small for now — kebab-case fits how canonical identifiers tend to be
/// used in JSON APIs. Additional casings (snake-case, camelCase) can be added here without
/// changing call sites.
/// </para>
/// </summary>
public class NormalizationOptions
{
    public const string SectionName = "Normalization";

    // Default to kebab-case canonicalisation so deploy-event senders with inconsistent casing
    // ("QA"/"qa", "TriggeredBy"/"triggered-by") converge on one stored form out of the box.
    // Explicitly set to null in appsettings to preserve sender casing.
    public string? Roles { get; set; } = "kebab-case";
    public string? Environments { get; set; } = "kebab-case";

    public bool ShouldNormalizeRoles => string.Equals(Roles, "kebab-case", StringComparison.OrdinalIgnoreCase);
    public bool ShouldNormalizeEnvironments => string.Equals(Environments, "kebab-case", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies role normalization per the configured policy, or returns the input unchanged
    /// (trimmed) when no policy is configured.
    /// </summary>
    public string ApplyRole(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return ShouldNormalizeRoles ? RoleNormalizer.Normalize(input) : input.Trim();
    }

    /// <summary>
    /// Applies environment normalization per the configured policy, or returns the input
    /// unchanged (trimmed) when no policy is configured. The same lower-kebab-case helper is
    /// used for both roles and environments — their shapes are similar enough (short
    /// identifiers, no punctuation) that a single canonicaliser is sufficient.
    /// </summary>
    public string ApplyEnvironment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return ShouldNormalizeEnvironments ? RoleNormalizer.Normalize(input) : input.Trim();
    }
}
