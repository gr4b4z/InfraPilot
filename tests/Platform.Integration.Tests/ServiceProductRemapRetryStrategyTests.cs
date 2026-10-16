using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// The remap has to survive a <b>retrying execution strategy</b>, because in production it always runs
/// under one: both providers are configured with <c>EnableRetryOnFailure</c> (Program.cs) so the first
/// deploy after an Azure serverless auto-pause can wait out a cold resume. EF forbids
/// <c>BeginTransaction</c> under such a strategy unless the whole transaction is wrapped in the
/// strategy's own <c>ExecuteAsync</c> — otherwise a retry would replay only part of the work.
///
/// <para>The rest of the suite cannot catch this. SQLite has no retrying strategy, so a transaction
/// opened directly works there and the regression only appears against a real database. This factory
/// gives the SQLite test context a strategy that reports <c>RetriesOnFailure</c>, which is the single
/// property EF's guard checks — enough to reproduce the production failure in-process.</para>
/// </summary>
public class ServiceProductRemapRetryStrategyTests
    : IClassFixture<ServiceProductRemapRetryStrategyTests.RetryingStrategyFactory>, IDisposable
{
    private const string TestApiKey = "test-remap-retry-key-12345";

    private readonly HttpClient _apiKeyClient;
    private readonly HttpClient _adminClient;

    public ServiceProductRemapRetryStrategyTests(RetryingStrategyFactory factory)
    {
        _apiKeyClient = factory.CreateClient();
        _apiKeyClient.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        _adminClient = factory.CreateAdminClient();
    }

    public void Dispose()
    {
        _apiKeyClient.Dispose();
        _adminClient.Dispose();
    }

    /// <summary>
    /// Applying a remap under a retrying strategy must work, not fail with
    /// "does not support user-initiated transactions".
    /// </summary>
    [Fact]
    public async Task Remap_AppliesUnderARetryingExecutionStrategy()
    {
        var suffix = $"{Guid.NewGuid():N}"[..8];
        var old = $"legacy-{suffix}";
        var target = $"target-{suffix}";
        var service = $"svc-retry-{suffix}";

        var ingested = await _apiKeyClient.PostAsJsonAsync("/api/deployments/events", new
        {
            product = old,
            service,
            environment = "staging",
            version = "v1.0.0",
            source = "ci",
            deployedAt = DateTimeOffset.UtcNow,
            status = "succeeded",
        });
        Assert.Equal(HttpStatusCode.Created, ingested.StatusCode);

        var saved = await _adminClient.PostAsJsonAsync("/api/deployments/admin/product-overrides",
            new { service, product = target, fromProduct = (string?)null, reason = (string?)null });
        saved.EnsureSuccessStatusCode();

        var rows = await _adminClient.GetFromJsonAsync<JsonElement>(
            "/api/deployments/admin/product-overrides");
        var id = rows.EnumerateArray()
            .First(r => r.GetProperty("service").GetString() == service)
            .GetProperty("id").GetGuid();

        var applied = await _adminClient.PostAsync(
            $"/api/deployments/admin/product-overrides/{id}/remap", null);

        // Before the fix this is a 500: EF refuses the user-initiated transaction.
        applied.EnsureSuccessStatusCode();
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("applied").GetBoolean());
        Assert.Equal(1, result.GetProperty("deployments").GetInt32());

        // And it really moved — a transaction that never committed would leave the event behind.
        var matrix = await _adminClient.GetFromJsonAsync<JsonElement>(
            $"/api/deployments/state?product={target}");
        Assert.Contains(service, matrix.EnumerateArray()
            .Select(s => s.GetProperty("service").GetString()!));
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// SQLite, but with an execution strategy that reports <c>RetriesOnFailure</c> the way the
    /// SQL Server and Npgsql retrying strategies do. It never actually retries — reproducing EF's
    /// guard needs only the property, and a strategy that swallowed failures would make every other
    /// assertion in this class unreliable.
    /// </summary>
    public class RetryingStrategyFactory : TestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "remap-retry-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);

            // Re-register on top of the base factory's SQLite registration, adding the strategy.
            builder.ConfigureServices(services =>
            {
                services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                    options.UseSqlite(
                        sp.GetRequiredService<DbConnection>(),
                        sqlite => sqlite.ExecutionStrategy(deps => new FakeRetryingExecutionStrategy(deps))));
            });
        }
    }

    private sealed class FakeRetryingExecutionStrategy : ExecutionStrategy
    {
        public FakeRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
        {
        }

        // Never retry: the point is to be a strategy that COULD, so EF applies its
        // user-transaction guard. Retrying for real would mask genuine failures.
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
