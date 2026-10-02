using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FreeSpace.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to build the model for migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PersistenceSetup.Configure(options, "Host=localhost;Database=freespace;Username=freespace;Password=design-time");
        return new AppDbContext(options.Options, NoTenantContext.Instance);
    }
}
