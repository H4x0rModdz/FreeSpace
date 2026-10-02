using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Identity;

public enum UserStatus
{
    Active,
    Disabled,
}

public sealed class User : Entity
{
    private User() { }

    public User(string email, string name, string? passwordHash, DateTimeOffset now)
    {
        Email = email.Trim();
        NormalizedEmail = NormalizeEmail(email);
        Name = name.Trim();
        PasswordHash = passwordHash;
        CreatedAt = now;
    }

    public string Email { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? PasswordHash { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => Status == UserStatus.Active;

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}
