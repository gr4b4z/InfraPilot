using Platform.Api.Features.Promotions;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// The overtake rule closes promotions based on this comparison, so a wrong answer in the permissive
/// direction retires a promotion that was still going out. Refusing to order is always the safe result;
/// these guard that it refuses whenever it should.
/// </summary>
public class PromotionVersionOrderTests
{
    [Theory]
    [InlineData("6.0.5-g1234abcd", "6.0.4-g03ce8515")] // the real production shape
    [InlineData("6.0.10-gaaa", "6.0.9-gbbb")]          // numeric, not lexicographic
    [InlineData("1.94.0", "1.93.9")]
    [InlineData("v2.0.0", "v1.9.9")]                   // "v" prefix is decoration
    [InlineData("2.0.0", "v1.9.9")]                    // mixed conventions still compare
    [InlineData("6.1", "6.0.9")]
    public void IsNewerThan_True(string newer, string older)
    {
        Assert.True(PromotionVersionOrder.IsNewerThan(newer, older));
        Assert.False(PromotionVersionOrder.IsNewerThan(older, newer));
    }

    [Theory]
    [InlineData("6.0.0", "6.0")]        // missing components count as zero
    [InlineData("6.0.4-gaaa", "6.0.4-gbbb")] // suffix is build identity, not ordering
    public void IsNewerThan_EqualVersions_False(string left, string right)
    {
        Assert.False(PromotionVersionOrder.IsNewerThan(left, right));
        Assert.False(PromotionVersionOrder.IsNewerThan(right, left));
        Assert.True(PromotionVersionOrder.TryCompare(left, right, out var cmp));
        Assert.Equal(0, cmp);
    }

    [Theory]
    [InlineData("release-candidate", "6.0.4")]
    [InlineData("6.0.4", "main")]
    [InlineData("g03ce8515", "6.0.4")]
    [InlineData("7", "8")]              // single component: more likely a different scheme
    [InlineData("6.0.x", "6.0.4")]
    [InlineData("", "6.0.4")]
    [InlineData(null, "6.0.4")]
    public void TryCompare_Unorderable_ReturnsFalse(string? left, string right)
    {
        Assert.False(PromotionVersionOrder.TryCompare(left, right, out _));
        Assert.False(PromotionVersionOrder.TryCompare(right, left, out _));
        // And therefore never reports one as newer, in either direction.
        Assert.False(PromotionVersionOrder.IsNewerThan(left, right));
        Assert.False(PromotionVersionOrder.IsNewerThan(right, left));
    }
}
