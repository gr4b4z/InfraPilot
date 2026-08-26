using System.Text.Json;
using Platform.Api.Features.Promotions.Models;

namespace Platform.Api.Tests.Features.Promotions;

/// <summary>
/// Covers the backward-tolerant <see cref="GroupRefJsonConverter"/>: legacy bare-string data must
/// still deserialise (treated as id == name), the object form must round-trip, and the write form
/// must always be the { id, name } object.
/// </summary>
public class GroupRefJsonConverterTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Read_BareString_TreatsAsIdAndName()
    {
        var g = JsonSerializer.Deserialize<GroupRef>("\"InfraPortal.Admin\"", CamelCase)!;
        Assert.Equal("InfraPortal.Admin", g.Id);
        Assert.Equal("InfraPortal.Admin", g.Name);
    }

    [Fact]
    public void Read_Object_PreservesIdAndName()
    {
        var g = JsonSerializer.Deserialize<GroupRef>(
            "{\"id\":\"abc-123\",\"name\":\"Release Managers\"}", CamelCase)!;
        Assert.Equal("abc-123", g.Id);
        Assert.Equal("Release Managers", g.Name);
    }

    [Fact]
    public void Read_Object_MissingName_DefaultsToId()
    {
        var g = JsonSerializer.Deserialize<GroupRef>("{\"id\":\"abc-123\"}", CamelCase)!;
        Assert.Equal("abc-123", g.Id);
        Assert.Equal("abc-123", g.Name);
    }

    [Fact]
    public void Write_EmitsObjectForm()
    {
        var json = JsonSerializer.Serialize(new GroupRef("abc-123", "Release Managers"), CamelCase);
        Assert.Equal("{\"id\":\"abc-123\",\"name\":\"Release Managers\"}", json);
    }

    [Fact]
    public void Object_RoundTrips()
    {
        var original = new GroupRef("abc-123", "Release Managers");
        var json = JsonSerializer.Serialize(original, CamelCase);
        var back = JsonSerializer.Deserialize<GroupRef>(json, CamelCase)!;
        Assert.Equal(original, back);
    }

    [Fact]
    public void LegacyRequirementJson_WithBareStringGroups_StillDeserialises()
    {
        // The exact shape of an existing ApprovalStepsJson row written under the old model.
        const string legacy =
            "[{\"name\":\"Release\",\"requirements\":[" +
            "{\"name\":\"R\",\"groups\":[\"InfraPortal.Admin\"],\"users\":[],\"minApprovers\":2}]}]";

        var steps = JsonSerializer.Deserialize<List<ApprovalStep>>(legacy, CamelCase)!;
        var group = steps[0].Requirements[0].Groups[0];
        Assert.Equal("InfraPortal.Admin", group.Id);
        Assert.Equal("InfraPortal.Admin", group.Name);
    }
}
