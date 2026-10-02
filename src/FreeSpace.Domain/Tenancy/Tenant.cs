using FreeSpace.Domain.Common;

namespace FreeSpace.Domain.Tenancy;

public sealed class Tenant : Entity
{
    private Tenant() { }

    public Tenant(string name, DateTimeOffset now)
    {
        Name = name.Trim();
        CreatedAt = now;
    }

    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name) => Name = name.Trim();
}
