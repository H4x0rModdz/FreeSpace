using System.Linq.Expressions;
using FreeSpace.Domain.Auditing;
using FreeSpace.Domain.Common;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Identity;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FreeSpace.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string TenantFilter = "Tenant";

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<StorageAccount> StorageAccounts => Set<StorageAccount>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<StoredObject> StoredObjects => Set<StoredObject>();
    public DbSet<Replica> Replicas => Set<Replica>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    // Referenced by the tenant query filter; EF re-evaluates it per context instance.
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<UserStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TenantRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StorageProvider>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StorageAccountStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<NodeKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StoredObjectStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ReplicaStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UploadSessionStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UploadRoutingPolicy>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm"); // trigram index for name search
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantFilters(modelBuilder);
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var tenantOwnedTypes = modelBuilder.Model.GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType)
            .ToList();

        foreach (var clrType in tenantOwnedTypes)
        {
            // e => (Guid?)e.TenantId == this.CurrentTenantId
            var entity = Expression.Parameter(clrType, "e");
            var body = Expression.Equal(
                Expression.Convert(Expression.Property(entity, nameof(ITenantOwned.TenantId)), typeof(Guid?)),
                Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
            modelBuilder.Entity(clrType).HasQueryFilter(TenantFilter, Expression.Lambda(body, entity));
        }
    }
}
