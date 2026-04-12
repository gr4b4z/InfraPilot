namespace Platform.Api.Features.Catalog.Models;

public static class CatalogCategory
{
    public const string CiCd = "ci-cd";
    public const string Infrastructure = "infrastructure";
    public const string Access = "access";
    public const string Security = "security";
    public const string Monitoring = "monitoring";

    public static readonly IReadOnlyList<string> All = [CiCd, Infrastructure, Access, Security, Monitoring];
}
