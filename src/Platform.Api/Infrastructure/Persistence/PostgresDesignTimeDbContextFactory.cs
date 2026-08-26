using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> when running against the Postgres migration set.
/// <para>
/// Example:
/// <code>dotnet ef migrations add Foo --context PostgresPlatformDbContext --output-dir Migrations/Postgres</code>
/// </para>
/// The connection string is a placeholder — migrations are generated from the model, no live DB required.
/// </summary>
public class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresPlatformDbContext>
{
    public PostgresPlatformDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PostgresPlatformDbContext>();
        builder.UseNpgsql("Host=localhost;Database=design;Username=design;Password=design");
        return new PostgresPlatformDbContext(builder.Options);
    }
}
