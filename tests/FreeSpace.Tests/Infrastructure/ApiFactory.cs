using FreeSpace.Infrastructure.Storage.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace FreeSpace.Tests.Infrastructure;

/// <summary>Boots the API against throwaway Postgres and S3 containers (requires Docker). Google is faked.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public S3TestServer S3 { get; } = new();
    public FakeGoogleApi Google { get; } = new();

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), S3.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await S3.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789abcdef-0123456789abcdef");
        builder.UseSetting("Encryption:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "100000");
        builder.UseSetting("RateLimiting:PublicPermitsPerMinute", "100000");
        builder.UseSetting("Storage:BackgroundQuotaSync", "false");
        builder.UseSetting("Storage:BackgroundPurge", "false"); // tests drive the purger directly
        builder.UseSetting("Storage:BackgroundUploadExpiry", "false");
        // The S3 test server runs on localhost over HTTP; production defaults reject both.
        builder.UseSetting("Storage:S3:AllowPrivateEndpoints", "true");
        builder.UseSetting("Storage:S3:AllowInsecureEndpoints", "true");

        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IGoogleApi>(Google)));
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
