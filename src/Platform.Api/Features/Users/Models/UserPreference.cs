namespace Platform.Api.Features.Users.Models;

/// <summary>
/// One preference belonging to one person. The per-user counterpart to the global
/// <see cref="Infrastructure.Features.PlatformSetting"/> key/value table, and deliberately the same
/// shape: an opaque string value under a namespaced key, so a new preference costs a key rather
/// than a migration.
///
/// <para>Keyed on the user's <b>email, lower-cased</b> — the same identity every other per-person
/// row in this system uses (<c>PromotionApproval.ApproverEmail</c>,
/// <c>WorkItemComment.AuthorEmail</c>, …). Email rather than <c>ICurrentUser.Id</c> because the id's
/// value space differs between the local-JWT and Entra auth modes, so a preference saved under one
/// would be invisible under the other; email is stable across both.</para>
/// </summary>
public class UserPreference
{
    public Guid Id { get; set; }

    /// <summary>Owning user, lower-cased email. See <c>UserPreferencesService.NormalizeEmail</c>.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>Namespaced preference key, e.g. <c>ui.hidden-products</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>Opaque to the store; each key's owner decides how to encode it.</summary>
    public string Value { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Known <see cref="UserPreference.Key"/> values.</summary>
public static class UserPreferenceKeys
{
    /// <summary>
    /// Products this user has hidden from every list in the app. JSON array of product names.
    ///
    /// <para>Stores what is <b>hidden</b>, not what is shown, so a product onboarded after the user
    /// made their choice still appears. The alternative — storing the visible set — means a new
    /// product is invisible to everyone who ever touched this setting, and nobody knows to look
    /// for it.</para>
    /// </summary>
    public const string HiddenProducts = "ui.hidden-products";
}
