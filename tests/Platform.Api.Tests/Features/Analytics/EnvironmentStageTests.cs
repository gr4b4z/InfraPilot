using Platform.Api.Features.Analytics;

namespace Platform.Api.Tests.Features.Analytics;

public class EnvironmentStageTests
{
    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("PROD")]
    [InlineData("prd")]
    [InlineData("live")]
    [InlineData("prod-eu")]
    [InlineData("production_us")]
    [InlineData("prod.apac")]
    public void IsProductionByName_Matches(string key)
        => Assert.True(EnvironmentStage.IsProductionByName(key));

    [Theory]
    [InlineData("preprod")]
    [InlineData("pre-prod")]
    [InlineData("dev")]
    [InlineData("test")]
    [InlineData("staging")]
    [InlineData("produkt")] // "prod" + letter, no separator — not production
    [InlineData("cloudiq_dev")]
    public void IsProductionByName_DoesNotMatch(string key)
        => Assert.False(EnvironmentStage.IsProductionByName(key));

    [Fact]
    public void DefaultRank_OrdersStages()
    {
        var keys = new[] { "prod", "dev", "staging", "test", "cloudiq_custom" };
        var ordered = keys.OrderBy(EnvironmentStage.DefaultRank).ToArray();
        Assert.Equal(["dev", "test", "staging", "cloudiq_custom", "prod"], ordered);
    }
}
