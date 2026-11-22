using Platform.Api.Features.ServiceInventory;
using Platform.Api.Features.ServiceInventory.Documents;

namespace Platform.Api.Tests.Features.ServiceInventory;

/// <summary>
/// The demo artifacts in samples/service-inventory are what a dev or demo instance reads. They must
/// stay in the published format, or the instance would keep serving its previous copy.
/// </summary>
public class DemoSampleFilesTests
{
    private static readonly string Dir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../samples/service-inventory"));

    public static TheoryData<string, string> Files()
    {
        var data = new TheoryData<string, string> { { "catalog", "catalog.json" }, { "status", "status.json" } };
        foreach (var file in Directory.GetFiles(Dir, "cost*.json").Order())
            data.Add("cost", Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_demo_file_is_accepted_and_projected(string type, string file)
    {
        var result = DocumentReader.Read(type, File.ReadAllBytes(Path.Combine(Dir, file)));
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(InventoryProjector.Project(Guid.NewGuid(), type, result.Document!));
    }

    [Fact]
    public void Closed_months_are_complete_and_the_latest_file_is_the_open_month()
    {
        foreach (var file in Directory.GetFiles(Dir, "cost-*.json"))
        {
            var doc = (CostDocument)DocumentReader.Read("cost", File.ReadAllBytes(file)).Document!;
            Assert.True(doc.Complete, file);
            Assert.Equal(Path.GetFileNameWithoutExtension(file)["cost-".Length..], doc.Period);
        }
        var latest = (CostDocument)DocumentReader.Read("cost", File.ReadAllBytes(Path.Combine(Dir, "cost.json"))).Document!;
        Assert.False(latest.Complete);
    }
}
