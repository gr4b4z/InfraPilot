using Microsoft.EntityFrameworkCore;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Provider-specific subclass used to disambiguate the SQL Server migration set.
/// All schema + behaviour is inherited from <see cref="PlatformDbContext"/>.
/// </summary>
public class SqlServerPlatformDbContext : PlatformDbContext
{
    public SqlServerPlatformDbContext(DbContextOptions<SqlServerPlatformDbContext> options) : base(options) { }
}
