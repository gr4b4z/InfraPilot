using Microsoft.EntityFrameworkCore;

namespace Platform.Api.Infrastructure.Persistence;

/// <summary>
/// Provider-specific subclass used to disambiguate the Postgres migration set.
/// All schema + behaviour is inherited from <see cref="PlatformDbContext"/>.
/// </summary>
public class PostgresPlatformDbContext : PlatformDbContext
{
    public PostgresPlatformDbContext(DbContextOptions<PostgresPlatformDbContext> options) : base(options) { }
}
