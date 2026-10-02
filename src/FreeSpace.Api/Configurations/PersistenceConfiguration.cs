using FreeSpace.Api.Auth;
using FreeSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FreeSpace.Api.Configurations;

internal static class PersistenceConfiguration
{
    public static IServiceCollection AddFreeSpacePersistence(this IServiceCollection services)
    {
        // The tenant of the current request scopes every tenant-owned query (global EF filter).
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<CurrentUser>());

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
            PersistenceSetup.Configure(options, connectionString);
        });

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
        return services;
    }

    /// <summary>Applies pending EF migrations when <c>Database:MigrateOnStartup</c> is on (Docker, dev).</summary>
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup) return;

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
}
