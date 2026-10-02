using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Persistence;

public static class PersistenceSetup
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
