using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Platform.Integration.Tests;

/// <summary>
/// The MCP server at <c>/api/mcp</c>, driven through the official MCP client over the test host:
/// <list type="bullet">
///   <item>It is behind the same gate as the API groups — no credential, no server.</item>
///   <item>Tools are listed with honest annotations, so clients can auto-allow the reads.</item>
///   <item>Names an agent gets slightly wrong resolve and say so; names it gets badly wrong fail
///         with the real names, rather than returning an empty result that reads as "nothing there".</item>
///   <item>The failure path — find a failed deploy, read its log, diagnose it — works end to end.</item>
///   <item>Pipeline-only writes keep the REST rules: a configured key with the scope.</item>
/// </list>
/// </summary>
public class McpServerTests : IClassFixture<McpServerTests.McpFactory>
{
    public class McpFactory : TestFactory
    {
        public const string PipelineKey = "mcp-pipeline-key-12345";
        public const string BuildsOnlyKey = "mcp-builds-only-key-12345";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "mcp-pipeline");
            builder.UseSetting("Deployments:ApiKeys:0:Key", PipelineKey);
            // A scoped key without promotion:create.
            builder.UseSetting("Deployments:ApiKeys:1:Name", "mcp-builds-only");
            builder.UseSetting("Deployments:ApiKeys:1:Key", BuildsOnlyKey);
            builder.UseSetting("Deployments:ApiKeys:1:Scopes:0", "build:register");
        }
    }

    private const string Product = "mcp-shop";
    private const string Service = "checkout-api";

    private readonly McpFactory _factory;

    public McpServerTests(McpFactory factory)
    {
        _factory = factory;
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>Idempotent — ingest replays on the natural key — so every test can call it.</summary>
    private async Task SeedAsync()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", McpFactory.PipelineKey);

        async Task Post(object body) =>
            (await client.PostAsJsonAsync("/api/deployments/events", body)).EnsureSuccessStatusCode();

        await Post(new { product = Product, service = Service, environment = "staging", version = "1.4.0", source = "test", deployedAt = DateTimeOffset.UtcNow.AddHours(-3) });
        await Post(new { product = Product, service = Service, environment = "production", version = "1.3.0", source = "test", deployedAt = DateTimeOffset.UtcNow.AddHours(-30) });
        await Post(new { product = Product, service = "checkout-worker", environment = "staging", version = "2.0.0", source = "test", deployedAt = DateTimeOffset.UtcNow.AddHours(-2) });
        await Post(new
        {
            product = Product, service = Service, environment = "test", version = "1.5.0", source = "test",
            deployedAt = DateTimeOffset.UtcNow.AddHours(-1), status = "failed",
            run = new { provider = "github-actions", failureReason = "pod checkout-api cannot start (ErrImagePull)" },
            logs = new[] { new { name = "helm", content = "helm upgrade --install\nWaiting for rollout\nError: ImagePullBackOff for checkout-api:1.5.0\ndone" } },
        });
    }

    private async Task<McpClient> ConnectAsync(string apiKey)
    {
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/api/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
        }, http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? []);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, text);
    }

    private static async Task<JsonElement> CallJsonAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var (isError, text) = await CallAsync(client, tool, args);
        Assert.False(isError, $"{tool} failed: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task Rejects_a_request_without_credentials()
    {
        using var http = _factory.CreateClient();
        var initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = new StringContent(initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Lists_the_tools_with_reads_marked_read_only()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "whoami", "get_deployment_state", "get_deployment", "list_promotions", "diagnose_promotion", "diagnose_deployment", "search_knowledge", "promote_build" })
            Assert.Contains(name, tools.Keys);
        Assert.True(tools["get_deployment_state"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["diagnose_promotion"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tools["create_promotion"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInstructions));
    }

    [Fact]
    public async Task Deployment_state_resolves_a_misspelt_service_and_says_what_it_assumed()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var state = await CallJsonAsync(client, "get_deployment_state", new() { ["service"] = "chekout-api" });

        var row = Assert.Single(state.GetProperty("services").EnumerateArray());
        Assert.Equal(Product, row.GetProperty("product").GetString());
        var environments = row.GetProperty("environments");
        Assert.Equal("1.4.0", environments.GetProperty("staging").GetProperty("version").GetString());
        Assert.Equal("1.3.0", environments.GetProperty("production").GetProperty("version").GetString());
        Assert.Contains("checkout-api", state.GetProperty("nameResolution")[0].GetString());
        Assert.StartsWith("http", row.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_service_fragment_answers_for_every_service_it_matches()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var state = await CallJsonAsync(client, "get_deployment_state", new() { ["service"] = "checkout" });

        var services = state.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("service").GetString()).ToList();
        Assert.Equal(["checkout-api", "checkout-worker"], services);
        Assert.Contains("2 services", state.GetProperty("nameResolution")[0].GetString());
    }

    [Fact]
    public async Task Results_are_not_html_escaped()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var (_, text) = await CallAsync(client, "get_service", new() { ["service"] = Service });

        Assert.DoesNotContain("\\u002B", text);
        Assert.Contains("+00:00", text);
    }

    [Fact]
    public async Task An_unknown_product_fails_with_the_products_that_exist()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var (isError, text) = await CallAsync(client, "get_deployment_state", new() { ["product"] = "no-such-thing-at-all" });

        Assert.True(isError);
        Assert.Contains(Product, text);
    }

    [Fact]
    public async Task A_failed_deploy_can_be_found_read_and_diagnosed()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var recent = await CallJsonAsync(client, "list_recent_deployments", new() { ["product"] = Product, ["status"] = "failed" });
        var failed = Assert.Single(recent.GetProperty("deployments").EnumerateArray());
        Assert.Equal("pod checkout-api cannot start (ErrImagePull)", failed.GetProperty("failureReason").GetString());
        var eventId = failed.GetProperty("id").GetString()!;

        var detail = await CallJsonAsync(client, "get_deployment", new() { ["eventId"] = eventId });
        var logId = detail.GetProperty("logs")[0].GetProperty("id").GetString()!;

        var log = await CallJsonAsync(client, "get_deployment_log", new() { ["eventId"] = eventId, ["logId"] = logId, ["match"] = "imagepull" });
        Assert.Equal(1, log.GetProperty("returnedLines").GetInt32());
        Assert.Equal("3: Error: ImagePullBackOff for checkout-api:1.5.0", log.GetProperty("content").GetString());

        var diagnosis = await CallJsonAsync(client, "diagnose_deployment", new() { ["eventId"] = eventId });
        Assert.Equal(eventId, diagnosis.GetProperty("deployment").GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_bad_id_tells_the_agent_where_to_find_a_good_one()
    {
        await using var client = await ConnectAsync(McpFactory.PipelineKey);

        var (isError, text) = await CallAsync(client, "get_promotion", new() { ["promotionId"] = "MPT-1234" });

        Assert.True(isError);
        Assert.Contains("list_promotions", text);
    }

    [Fact]
    public async Task Creating_a_promotion_needs_the_promotion_create_scope()
    {
        await using var client = await ConnectAsync(McpFactory.BuildsOnlyKey);

        var (isError, text) = await CallAsync(client, "create_promotion", new()
        {
            ["product"] = Product, ["service"] = Service, ["sourceEnv"] = "staging", ["targetEnv"] = "production", ["version"] = "1.4.0",
        });

        Assert.True(isError);
        Assert.Contains("promotion:create", text);
    }

    [Fact]
    public async Task Whoami_names_a_configured_key_and_what_it_may_do()
    {
        await using var client = await ConnectAsync(McpFactory.BuildsOnlyKey);

        var me = await CallJsonAsync(client, "whoami");

        Assert.Equal("mcp-builds-only", me.GetProperty("name").GetString());
        Assert.Equal("api_key", me.GetProperty("authMethod").GetString());
        Assert.False(me.GetProperty("can").GetProperty("createPromotion").GetBoolean());
    }

    [Fact]
    public async Task A_personal_token_acts_as_its_owner_without_admin_or_pipeline_rights()
    {
        using var admin = _factory.CreateAdminClient();
        var created = await admin.PostAsJsonAsync("/api/me/tokens", new { name = "mcp test", expiresInDays = 7 });
        created.EnsureSuccessStatusCode();
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        await using var client = await ConnectAsync(secret);
        var me = await CallJsonAsync(client, "whoami");
        Assert.Equal("personal_token", me.GetProperty("authMethod").GetString());
        Assert.Equal("admin@localhost", me.GetProperty("email").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());

        var (isError, text) = await CallAsync(client, "create_promotion", new()
        {
            ["product"] = Product, ["service"] = Service, ["sourceEnv"] = "staging", ["targetEnv"] = "production", ["version"] = "1.4.0",
        });
        Assert.True(isError);
        Assert.Contains("pipeline", text);

        // Reads work as for any key.
        var state = await CallJsonAsync(client, "get_deployment_state", new() { ["product"] = Product });
        Assert.NotEmpty(state.GetProperty("services").EnumerateArray());
    }
}
