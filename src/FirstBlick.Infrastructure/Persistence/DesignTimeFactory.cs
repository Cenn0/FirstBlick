using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FirstBlick.Infrastructure.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FirstBlickDbContext>
{
    public FirstBlickDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FIRSTBLICK_MIGRATION_CONNECTION")
            ?? "Host=localhost;Database=firstblick;Username=firstblick";
        return new FirstBlickDbContext(new DbContextOptionsBuilder<FirstBlickDbContext>().UseNpgsql(connection).Options, new TenantScope());
    }
}
