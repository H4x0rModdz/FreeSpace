using FreeSpace.Domain.Auditing;
using FreeSpace.Domain.Files;
using FreeSpace.Domain.Identity;
using FreeSpace.Domain.Storage;
using FreeSpace.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreeSpace.Infrastructure.Persistence;

internal static class Lengths
{
    public const int Email = 320;
    public const int Name = 200;
    public const int Hash = 128;
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(x => x.Email).HasMaxLength(Lengths.Email);
        b.Property(x => x.NormalizedEmail).HasMaxLength(Lengths.Email);
        b.Property(x => x.Name).HasMaxLength(Lengths.Name);
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Ignore(x => x.IsActive);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions");
        b.Property(x => x.RefreshTokenHash).HasMaxLength(Lengths.Hash);
        b.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(Lengths.Hash);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.RevokedReason).HasMaxLength(64);
        b.HasIndex(x => x.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        // Maps to Postgres xmin: concurrent refreshes of the same session fail instead of racing.
        b.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(x => x.Name).HasMaxLength(Lengths.Name);
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.Property(x => x.Email).HasMaxLength(Lengths.Email);
        b.Property(x => x.TokenHash).HasMaxLength(Lengths.Hash);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.TenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.InvitedByUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.Property(x => x.Action).HasMaxLength(64);
        b.Property(x => x.TargetType).HasMaxLength(64);
        b.Property(x => x.DataJson).HasColumnName("data").HasColumnType("jsonb");
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        // Actor is kept loosely coupled so audit history survives user deletion.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class StorageAccountConfiguration : IEntityTypeConfiguration<StorageAccount>
{
    public void Configure(EntityTypeBuilder<StorageAccount> b)
    {
        b.Property(x => x.ExternalAccountId).HasMaxLength(512);
        b.Property(x => x.DisplayName).HasMaxLength(Lengths.Name);
        b.Property(x => x.Email).HasMaxLength(Lengths.Email);
        b.Property(x => x.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Ignore(x => x.AvailableBytes);
        b.Ignore(x => x.FreeBytes);
        b.HasIndex(x => new { x.TenantId, x.Provider, x.ExternalAccountId }).IsUnique();
        b.HasIndex(x => new { x.Status, x.LastQuotaSyncAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OAuthStateConfiguration : IEntityTypeConfiguration<OAuthState>
{
    public void Configure(EntityTypeBuilder<OAuthState> b)
    {
        b.ToTable("oauth_states");
        b.Property(x => x.StateHash).HasMaxLength(Lengths.Hash);
        b.Property(x => x.ReturnUrl).HasMaxLength(2048);
        b.HasIndex(x => x.StateHash).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NodeConfiguration : IEntityTypeConfiguration<Node>
{
    public void Configure(EntityTypeBuilder<Node> b)
    {
        b.Property(x => x.Name).HasMaxLength(NodeName.MaxLength);
        b.Property(x => x.NormalizedName).HasMaxLength(NodeName.MaxLength);
        b.Property(x => x.MimeType).HasMaxLength(255);
        b.Ignore(x => x.IsFolder);
        b.Ignore(x => x.IsTrashed);

        // Live siblings must have distinct names; root items (parent NULL) count as siblings too.
        b.HasIndex(x => new { x.TenantId, x.ParentId, x.NormalizedName })
            .IsUnique()
            .HasFilter("trashed_at IS NULL")
            .AreNullsDistinct(false);
        b.HasIndex(x => new { x.TenantId, x.TrashRootId });
        b.HasIndex(x => x.ParentId);
        b.HasIndex(x => x.ObjectId);
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        // Subtrees are deleted in one statement, which NO ACTION allows (checked at statement end).
        b.HasOne<Node>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<StoredObject>().WithMany().HasForeignKey(x => x.ObjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StoredObjectConfiguration : IEntityTypeConfiguration<StoredObject>
{
    public void Configure(EntityTypeBuilder<StoredObject> b)
    {
        b.ToTable("stored_objects");
        b.Property(x => x.MimeType).HasMaxLength(255);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.HasIndex(x => x.Status);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReplicaConfiguration : IEntityTypeConfiguration<Replica>
{
    public void Configure(EntityTypeBuilder<Replica> b)
    {
        b.Property(x => x.ProviderObjectId).HasMaxLength(1024);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.ObjectId);
        b.HasIndex(x => x.StorageAccountId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<StoredObject>().WithMany().HasForeignKey(x => x.ObjectId).OnDelete(DeleteBehavior.Cascade);
        // An account with replicas cannot be removed (checked by the API); NO ACTION lets tenant deletion cascade.
        b.HasOne<StorageAccount>().WithMany().HasForeignKey(x => x.StorageAccountId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> b)
    {
        b.Property(x => x.FileName).HasMaxLength(NodeName.MaxLength);
        b.Property(x => x.MimeType).HasMaxLength(255);
        b.Property(x => x.ObjectKey).HasMaxLength(1024);
        b.Ignore(x => x.ChunkCount);
        b.HasIndex(x => new { x.Status, x.ExpiresAt });
        b.HasIndex(x => x.StorageAccountId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<StorageAccount>().WithMany().HasForeignKey(x => x.StorageAccountId).OnDelete(DeleteBehavior.NoAction);
        // No FK to the object: aborting deletes the pending object while the session row stays as history.
        // Row version: two concurrent "complete" calls must not both publish the file.
        b.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class ShareConfiguration : IEntityTypeConfiguration<Share>
{
    public void Configure(EntityTypeBuilder<Share> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(Lengths.Hash);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.NodeId });
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        // Deleting the node forever deletes its links; trashing it just makes them stop working.
        b.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
