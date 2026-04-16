using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// SQLite doesn't support <c>DateTimeOffset</c> in ORDER BY or WHERE comparisons. This subclass
/// registers a convention that converts all <c>DateTimeOffset</c> properties to ticks (long) so
/// queries work correctly against the in-memory SQLite test database.
/// </summary>
public class SqliteTestDbContext : PlatformDbContext
{
    public SqliteTestDbContext(DbContextOptions<SqliteTestDbContext> options) : base(options) { }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<long>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<long>();
    }
}

/// <summary>
/// Base test factory that configures the test server with an in-memory SQLite database and
/// local-JWT auth. Subclass this to add test-specific configuration.
/// </summary>
public class TestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public TestFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new SqliteTestDbContext(options);
        db.Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove the real DB registrations.
            RemoveService<DbContextOptions<PostgresPlatformDbContext>>(services);
            RemoveService<DbContextOptions<SqlServerPlatformDbContext>>(services);
            RemoveService<DbContextOptions<PlatformDbContext>>(services);
            RemoveService<PostgresPlatformDbContext>(services);
            RemoveService<SqlServerPlatformDbContext>(services);
            RemoveService<PlatformDbContext>(services);

            services.AddSingleton<DbConnection>(_connection);
            // Register SqliteTestDbContext (with DateTimeOffset->long conversion) as PlatformDbContext.
            services.AddDbContext<PlatformDbContext, SqliteTestDbContext>((sp, options) =>
                options.UseSqlite(sp.GetRequiredService<DbConnection>()));
        });
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> authenticated as admin@localhost (InfraPortal.Admin role).
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email = "admin@localhost", password = "admin123" })
            .GetAwaiter().GetResult();
        loginResponse.EnsureSuccessStatusCode();
        var stream = loginResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(stream);
        var token = doc.RootElement.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    protected static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
