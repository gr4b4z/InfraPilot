using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Infrastructure.Persistence;

// Test classes run in parallel (xUnit's default: one collection per class; the tests inside a class
// still run one at a time). That is safe because a host shares nothing with the others: each owns
// its in-memory database, and the feature-flag cache is a singleton in the host's container rather
// than a static. Keep it that way — process-wide mutable state in the API (a static cache, a static
// setting) makes tests in different classes interfere, and the failures look random.

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
/// In-memory SQLite databases that already have the test schema, one per test host.
///
/// <para><c>EnsureCreated</c> costs 30–100 ms: EF turns the model into DDL and runs it one
/// statement at a time. Most test classes build a host per test, so that cost was paid a few
/// hundred times a run. Here it runs once per test process, on a template, and each database is a
/// page-for-page copy of the template made with SQLite's backup API, which takes under a
/// millisecond.</para>
/// </summary>
public static class TestDatabase
{
    private static readonly Lazy<SqliteConnection> Template = new(() =>
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SqliteTestDbContext>()
            .UseSqlite(connection)
            .Options;
        using var db = new SqliteTestDbContext(options);
        db.Database.EnsureCreated();
        return connection;
    });

    /// <summary>
    /// Opens a new private in-memory database with the schema in place. The database lives as long
    /// as the returned connection stays open.
    /// </summary>
    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        // A connection isn't thread-safe, and test classes build their hosts in parallel.
        lock (Template) Template.Value.BackupDatabase(connection);
        return connection;
    }
}

/// <summary>
/// Isolation every test host needs, whichever factory builds it. Call
/// <see cref="Apply"/> from the factory's <c>ConfigureServices</c>.
/// <see cref="TestHostIsolationTests"/> checks that every factory in this assembly does.
/// </summary>
public static class TestHostIsolation
{
    /// <summary>
    /// Removes the API's own hosted background workers, and makes password hashing cheap.
    ///
    /// <para><b>Why the workers have to go.</b> A test host keeps its in-memory database alive
    /// on a single <see cref="SqliteConnection"/>, and every DbContext in the host shares it.
    /// That connection is not thread-safe. EF's SQLite provider also re-registers its SQL
    /// functions on the connection each time it builds a DbContext, and SQLite refuses that while
    /// any statement is running ("SQLite Error 5: unable to delete/modify user-function due to
    /// active statements", or "database is locked"). The workers run on their own timers. They
    /// include the webhook delivery pump, which runs every 5 s; the deploy-event work-item
    /// backfill, which runs once 5 s after start; the executor poller, which runs every 10 s;
    /// and the escalation timer. Whenever one of them was mid-query as a request opened its
    /// DbContext, that request failed with a 500, and whichever test it belonged to failed. That
    /// was usually the login in a test class constructor. The same race showed up in the other
    /// direction as errors that the worker logged and swallowed. Production has a pooled
    /// connection per context, so it does not have this problem. Tests that exercise a worker
    /// drive it directly, so nothing depends on the timers.</para>
    ///
    /// <para>Framework hosted services (the web server itself, data protection's key-ring warm-up)
    /// are kept; only types declared in the API assembly are removed, so a worker added later is
    /// covered without touching this list.</para>
    ///
    /// <para><b>Why hashing is turned down.</b> The local users' passwords are PBKDF2 with 100,000
    /// iterations. That is about 50 ms per hash. Every host seeds four users, and every login
    /// verifies one. Together that came to a quarter of a second or more per host. One iteration
    /// runs the same code.</para>
    /// </summary>
    public static void Apply(IServiceCollection services)
    {
        var apiAssembly = typeof(Program).Assembly;
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                     && d.ImplementationType?.Assembly == apiAssembly)
            .ToList();
        foreach (var d in workers) services.Remove(d);

        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1);
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
        _connection = TestDatabase.Open();
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

            TestHostIsolation.Apply(services);
        });
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> authenticated as admin@localhost (InfraPortal.Admin role).
    /// </summary>
    public HttpClient CreateAdminClient() => CreateAuthenticatedClient("admin@localhost", "admin123");

    /// <summary>
    /// Creates an <see cref="HttpClient"/> authenticated as one of the seeded local users. Needed
    /// wherever a test has to prove that per-user state stays per-user.
    /// </summary>
    public HttpClient CreateAuthenticatedClient(string email, string password)
    {
        var client = CreateClient();
        var loginResponse = client.PostAsJsonAsync("/api/auth/login", new { email, password })
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
