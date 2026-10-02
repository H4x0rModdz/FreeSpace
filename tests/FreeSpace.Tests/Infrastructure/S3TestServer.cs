using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace FreeSpace.Tests.Infrastructure;

/// <summary>
/// S3-compatible server for tests (SeaweedFS; MinIO no longer publishes public images).
/// Configured with a single identity so wrong credentials are actually rejected.
/// </summary>
public sealed class S3TestServer : IAsyncDisposable
{
    private const int S3Port = 8333;
    public const string AccessKey = "freespace-test";
    public const string SecretKey = "freespace-test-secret-key";

    private const string IdentitiesJson = $$"""
        {"identities":[{"name":"test","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
        """;

    private readonly IContainer _container = new ContainerBuilder("chrislusf/seaweedfs:latest")
        .WithResourceMapping(Encoding.UTF8.GetBytes(IdentitiesJson), "/etc/seaweedfs/s3.json")
        .WithCommand("server", "-dir=/data", "-s3", $"-s3.port={S3Port}", "-s3.config=/etc/seaweedfs/s3.json")
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(S3Port))
        .Build();

    public Task StartAsync() => _container.StartAsync();

    public string GetConnectionString() => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
