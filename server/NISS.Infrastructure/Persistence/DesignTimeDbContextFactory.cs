using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NISS.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c>. Connection string: env <c>NISS_Database__ConnectionString</c> or the local dev database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("NISS_Database__ConnectionString")
            ?? "Host=127.0.0.1;Port=5433;Database=niss_monitor;Username=postgres";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }
}
