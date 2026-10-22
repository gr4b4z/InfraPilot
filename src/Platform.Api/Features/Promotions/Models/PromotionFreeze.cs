namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// An active Promotion Freeze on one product's promotions into one target environment — the whole
/// product, or one of its services (see GLOSSARY.md, ADR-0001, ADR-0002, ADR-0003). While the row
/// exists no <see cref="PromotionCandidate"/> it covers can become Approved; lifting the freeze
/// deletes the row (the audit log keeps the history). At most one row per
/// <c>(Product, Service, TargetEnv)</c> — enforced by a unique index.
/// </summary>
public class PromotionFreeze
{
    /// <summary>The <see cref="Service"/> value of a whole-product freeze.</summary>
    public const string WholeProduct = "";

    public Guid Id { get; set; }
    public string Product { get; set; } = "";

    /// <summary>
    /// The frozen service, or <see cref="WholeProduct"/> (<c>""</c>) when the freeze covers every
    /// service of the product. The empty string rather than NULL so the unique index behaves the same
    /// on Postgres and SQL Server (ADR-0003).
    /// </summary>
    public string Service { get; set; } = WholeProduct;

    public string TargetEnv { get; set; } = "";
    public string Reason { get; set; } = "";
    public string FrozenBy { get; set; } = "";
    public string FrozenByEmail { get; set; } = "";
    public DateTimeOffset FrozenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this freeze covers every service of the product.</summary>
    public bool IsWholeProduct => Service == WholeProduct;

    /// <summary>Whether this freeze holds candidates of <paramref name="service"/> (the union rule of ADR-0003).</summary>
    public bool Covers(string service) => IsWholeProduct || Service == service;
}
