using FreeSpace.Domain.Auditing;
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
        b.HasIndex(x => x.StateHash).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
