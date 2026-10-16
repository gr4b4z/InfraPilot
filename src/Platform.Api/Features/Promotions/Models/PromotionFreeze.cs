namespace Platform.Api.Features.Promotions.Models;

/// <summary>
/// An active Promotion Freeze on one product's promotions into one target environment (see
/// GLOSSARY.md, ADR-0001, ADR-0002). While the row exists no <see cref="PromotionCandidate"/> for
/// <c>(Product, TargetEnv)</c> can become Approved; lifting the freeze deletes the row (the audit
/// log keeps the history). At most one row per pair — enforced by a unique index.
/// </summary>
public class PromotionFreeze
{
    public Guid Id { get; set; }
    public string Product { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public string Reason { get; set; } = "";
    public string FrozenBy { get; set; } = "";
    public string FrozenByEmail { get; set; } = "";
    public DateTimeOffset FrozenAt { get; set; } = DateTimeOffset.UtcNow;
}
