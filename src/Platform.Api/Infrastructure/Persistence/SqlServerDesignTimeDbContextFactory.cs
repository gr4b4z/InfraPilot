using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> when running against the SQL Server migration set.
/// <para>
/// Example:
/// <code>dotnet ef migrations add Foo --context SqlServerPlatformDbContext --output-dir Migrations/SqlServer</code>
/// </para>
/// The connection string is a placeholder — migrations are generated from the model, no live DB required.
/// </summary>
public class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqlServerPlatformDbContext>
{
    public SqlServerPlatformDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SqlServerPlatformDbContext>();
        builder.UseSqlServer("Server=localhost;Database=design;User Id=design;Password=design;TrustServerCertificate=True");
        return new SqlServerPlatformDbContext(builder.Options);
    }
}
