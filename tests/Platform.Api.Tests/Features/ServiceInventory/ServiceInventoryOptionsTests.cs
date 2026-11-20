using Platform.Api.Features.ServiceInventory;

namespace Platform.Api.Tests.Features.ServiceInventory;

public class ServiceInventoryOptionsTests
{
    private static ServiceInventoryOptions Valid() => new()
    {
        AllowedHosts = ["registry.example.com", "files.example.com"],
        Credentials = new(StringComparer.OrdinalIgnoreCase)
        {
            ["registry"] = new() { Kind = "basic", Username = "reader", Password = "x" },
            ["sas"] = new() { Kind = "query", Value = "sv=1&sig=abc" },
        },
        Feeds =
        [
            new() { Name = "catalog", Type = "catalog", Credential = "registry",
                Source = new() { Kind = "oci", Registry = "registry.example.com", Repository = "service-catalog/catalog", File = "catalog.json" } },
            new() { Name = "cost", Type = "cost", Credential = "sas",
                Source = new() { Kind = "https", Url = "https://files.example.com/cost/latest.json",
                    ClosedMonths = new() { From = "2026-11", UrlTemplate = "https://files.example.com/cost/{month}.json" } } },
        ],
    };

    [Fact]
    public void A_valid_configuration_has_no_errors()
    {
        Assert.Empty(ServiceInventoryOptionsValidator.Validate(Valid()));
    }

    [Fact]
    public void A_host_not_in_the_allowlist_is_rejected()
    {
        var options = Valid();
        options.Feeds[0].Source.Registry = "elsewhere.example.net";
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("not in AllowedHosts"));
    }

    [Fact]
    public void Plain_http_is_rejected()
    {
        var options = Valid();
        options.Feeds[1].Source.Url = "http://files.example.com/cost/latest.json";
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("must use https"));
    }

    [Fact]
    public void A_url_with_a_query_string_is_rejected_so_a_token_never_sits_in_plain_configuration()
    {
        var options = Valid();
        options.Feeds[1].Source.Url = "https://files.example.com/cost/latest.json?sig=abc";
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("query string"));
    }

    [Fact]
    public void An_unknown_credential_is_rejected()
    {
        var options = Valid();
        options.Feeds[0].Credential = "missing";
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("'missing' is not configured"));
    }

    [Fact]
    public void Two_feeds_of_the_same_type_are_rejected()
    {
        var options = Valid();
        options.Feeds.Add(new() { Name = "catalog-2", Type = "catalog",
            Source = new() { Kind = "oci", Registry = "registry.example.com", Repository = "x", File = "catalog.json" } });
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("only one catalog feed"));
    }

    [Fact]
    public void A_query_credential_on_an_oci_source_is_rejected()
    {
        var options = Valid();
        options.Feeds[0].Credential = "sas";
        Assert.Contains(ServiceInventoryOptionsValidator.Validate(options), e => e.Contains("query credential cannot be used with an oci source"));
    }

    [Fact]
    public void Closed_months_need_a_month_and_belong_to_cost_only()
    {
        var options = Valid();
        options.Feeds[1].Source.ClosedMonths!.From = "11/2026";
        options.Feeds[0].Source.ClosedMonths = new() { From = "2026-01" };
        var errors = ServiceInventoryOptionsValidator.Validate(options);
        Assert.Contains(errors, e => e.Contains("From must be YYYY-MM"));
        Assert.Contains(errors, e => e.Contains("only a cost feed has closed months"));
    }
}
