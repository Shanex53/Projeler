using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Logging.Abstractions;
using Nurol.Envanter.Server.Infrastructure;

namespace Nurol.Envanter.Tests;

/// <summary>
/// Evrak deposunun S3 uyumlu bir servisle (SeaweedFS) gerçek yazma/okuma testi.
/// Dosya içeriği bayt bayt aynı dönmelidir (parçalı imza veya sağlama toplamı eki olmadan).
/// </summary>
public class StorageTests
{
    [Fact]
    public async Task S3_store_round_trips_bytes_exactly()
    {
        await using var s3 = new ContainerBuilder()
            .WithImage("chrislusf/seaweedfs:3.80")
            .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-master.volumeSizeLimitMB=64")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForPath("/")))
            .Build();
        await s3.StartAsync();

        var store = new S3Store(new StorageOptions
        {
            Provider = "S3", ServiceUrl = $"http://{s3.Hostname}:{s3.GetMappedPublicPort(8333)}",
            Bucket = "test-evrak", AccessKey = "test", SecretKey = "test-gizli", CreateBucket = true
        });
        await store.EnsureBucketAsync(NullLogger.Instance);
        Assert.True(await store.PingAsync());

        var pdf = new byte[300_000];
        Random.Shared.NextBytes(pdf);
        "%PDF-1.7\n"u8.CopyTo(pdf);
        await store.PutAsync("evrak/test/tutanak.pdf", pdf, "application/pdf");
        Assert.Equal(pdf, await store.GetAsync("evrak/test/tutanak.pdf"));
    }
}
