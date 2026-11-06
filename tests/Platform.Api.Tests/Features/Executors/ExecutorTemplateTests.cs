using Platform.Api.Features.Executors;
using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Tests.Features.Executors;

public class ExecutorTemplateTests
{
    private static readonly ServiceRequest Request = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        RequesterEmail = "jane.doe@example.com",
        RequesterName = "Jane Doe",
    };

    [Fact]
    public void ResolvesInputsAndRequestVariables()
    {
        var map = new Dictionary<string, string>
        {
            ["environment"] = "{{environment}}",
            ["requesterEmail"] = "{{request.requester_email}}",
            ["requesterName"] = "{{request.requester_name}}",
            ["requestId"] = "{{request.id}}",
            ["label"] = "{{database}} for {{request.requester_name}}",
        };
        var inputs = new Dictionary<string, string> { ["environment"] = "test", ["database"] = "billing" };

        var resolved = ExecutorTemplate.ResolveParameters(map, inputs, Request);

        Assert.Equal("test", resolved["environment"]);
        Assert.Equal("jane.doe@example.com", resolved["requesterEmail"]);
        Assert.Equal("Jane Doe", resolved["requesterName"]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", resolved["requestId"]);
        Assert.Equal("billing for Jane Doe", resolved["label"]);
    }

    [Fact]
    public void UnknownPlaceholders_StayAsWritten()
    {
        var map = new Dictionary<string, string> { ["a"] = "{{missing}}", ["b"] = "{{request.email}}" };

        var resolved = ExecutorTemplate.ResolveParameters(map, new Dictionary<string, string>(), Request);

        Assert.Equal("{{missing}}", resolved["a"]);
        Assert.Equal("{{request.email}}", resolved["b"]);
    }

    [Fact]
    public void InputValues_AreNotExpandedAgain()
    {
        // Someone typing a placeholder into a form field must not get it resolved.
        var map = new Dictionary<string, string> { ["reason"] = "{{reason}}" };
        var inputs = new Dictionary<string, string> { ["reason"] = "{{request.requester_email}}" };

        var resolved = ExecutorTemplate.ResolveParameters(map, inputs, Request);

        Assert.Equal("{{request.requester_email}}", resolved["reason"]);
    }

    [Fact]
    public void InputNamedLikeARequestVariable_DoesNotOverrideIt()
    {
        var map = new Dictionary<string, string> { ["email"] = "{{request.requester_email}}" };
        var inputs = new Dictionary<string, string> { ["request.requester_email"] = "forged@example.com" };

        var resolved = ExecutorTemplate.ResolveParameters(map, inputs, Request);

        Assert.Equal("jane.doe@example.com", resolved["email"]);
    }

    [Fact]
    public void UnknownRequestVariables_ListsOnlyTypos()
    {
        var unknown = ExecutorTemplate.UnknownRequestVariables(
        [
            "{{request.id}}",
            "{{request.email}} and {{request.email}}",
            "{{environment}}",
            "{{request.requester_name}}-{{request.user}}",
        ]).ToList();

        Assert.Equal(["request.email", "request.user"], unknown);
    }
}
