using Platform.Api.Features.Migrations;

namespace Platform.Api.Tests.Features.Migrations;

public class MigrationNameMatcherTests
{
    private const string Name = "AlterAccountName";

    [Theory]
    [InlineData("AlterAccountName")]
    [InlineData("alteraccountname")]
    [InlineData("alter-account-name")]
    public void Exact_IgnoringCaseAndPunctuation_Scores100(string query) =>
        Assert.Equal(100, MigrationNameMatcher.Score(query, Name));

    [Theory]
    [InlineData("Alter")]           // prefix
    [InlineData("AccountName")]     // part of it
    [InlineData("account name")]    // words in any order
    [InlineData("name account")]
    [InlineData("AlterAcountName")] // a typo
    [InlineData("AltrAccountNam")]  // two
    [InlineData("altaccnm")]        // an abbreviation
    [InlineData("acount nmae")]     // words with typos
    public void Partial_AndFuzzy_Match(string query) =>
        Assert.True(MigrationNameMatcher.Score(query, Name) >= MigrationNameMatcher.Threshold, query);

    [Theory]
    [InlineData("Invoice")]
    [InlineData("xyz")]
    [InlineData("DropTable")]
    public void Unrelated_DoesNotMatch(string query) =>
        Assert.True(MigrationNameMatcher.Score(query, Name) < MigrationNameMatcher.Threshold, query);

    [Fact]
    public void MoreLiteralMatchesRankHigher()
    {
        var exact = MigrationNameMatcher.Score("AccountName", "AccountName");
        var prefix = MigrationNameMatcher.Score("AccountName", "AccountNameIndex");
        var contains = MigrationNameMatcher.Score("AccountName", Name);
        var typo = MigrationNameMatcher.Score("AcountName", Name);

        Assert.True(exact > prefix && prefix > contains && contains > typo);
    }

    [Fact]
    public void Words_SplitOnCaseDigitsAndSeparators() =>
        Assert.Equal(["add", "sql", "table", "v", "2"], MigrationNameMatcher.Words("AddSQLTable_v2"));
}
