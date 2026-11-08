using Amazon.S3;
using Amazon.S3.Model;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>Evrak dosyaları için özel depo. Üretimde S3 uyumlu servis (MinIO veya kurumsal), geliştirmede klasör.</summary>
public interface IFileStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default);
    Task<byte[]> GetAsync(string key, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public class StorageOptions
{
    /// <summary>"S3" veya "FileSystem".</summary>
    public string Provider { get; set; } = "FileSystem";
    public string Path { get; set; } = "data/evrak";
    public string? ServiceUrl { get; set; }
    public string Bucket { get; set; } = "nurol-envanter";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    /// <summary>Yerel deneme ortamında kovayı otomatik oluştur. Üretimde kova önceden hazırlanır.</summary>
    public bool CreateBucket { get; set; }
}

public class FileSystemStore(StorageOptions o) : IFileStore
{
    string Full(string key)
    {
        var root = System.IO.Path.GetFullPath(o.Path);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, key));
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Geçersiz dosya anahtarı");
        return full;
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        var path = Full(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Evrak dosyaları bir kez yazılır; var olan dosyanın üzerine yazılmaz.
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await fs.WriteAsync(content, ct);
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct = default) => File.ReadAllBytesAsync(Full(key), ct);

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetFullPath(o.Path));
        return Task.FromResult(true);
    }
}

public class S3Store : IFileStore
{
    readonly AmazonS3Client client;
    readonly StorageOptions o;

    public S3Store(StorageOptions o)
    {
        this.o = o;
        // S3 uyumlu servislerle (MinIO, SeaweedFS, kurumsal depolar) uyum için: parçalı imzalı gövde ve
        // varsayılan CRC sağlama toplamları yalnızca gerektiğinde kullanılır.
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = o.ForcePathStyle,
            AuthenticationRegion = o.Region,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (!string.IsNullOrEmpty(o.ServiceUrl)) cfg.ServiceURL = o.ServiceUrl;
        else cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        client = new AmazonS3Client(o.AccessKey, o.SecretKey, cfg);
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            // Anahtarlar sürüm + rastgele kimlik içerir; aynı anahtara ikinci kez yazılmaz.
            BucketName = o.Bucket, Key = key, InputStream = ms, ContentType = contentType,
            UseChunkEncoding = false
        }, ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct = default)
    {
        using var res = await client.GetObjectAsync(o.Bucket, key, ct);
        using var ms = new MemoryStream();
        await res.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await client.GetBucketLocationAsync(o.Bucket, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Yalnızca yerel/deneme ortamı için: kova yoksa oluşturur.</summary>
    public async Task EnsureBucketAsync(ILogger log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.PutBucketAsync(o.Bucket);
                return;
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { return; }
            catch (Exception ex) when (attempt < 10 && ex is HttpRequestException or Amazon.Runtime.AmazonServiceException)
            {
                log.LogWarning("Nesne deposuna ulaşılamadı, tekrar denenecek ({Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}

public static class UploadRules
{
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>Dosya içeriğinin imzasından gerçek türü belirler; uzantıya güvenmez.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF-"u8)) return "application/pdf";
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        return null;
    }

    public static string Extension(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        _ => ".bin"
    };
}
