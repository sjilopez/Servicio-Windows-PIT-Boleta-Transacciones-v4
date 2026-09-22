using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PITBoletaTransacciones.Services;

public interface IAzureBlobStorageService
{
    Task<AzureBlobUploadResult> UploadPdfAsync(string localPath, CancellationToken cancellationToken);
}

public sealed record AzureBlobUploadResult(
    string BlobPath,
    long Length,
    string LocalMd5,
    string RemoteMd5,
    bool AlreadyExisted);

public sealed class AzureBlobStorageService(IConfiguration configuration, ILogger<AzureBlobStorageService> logger) : IAzureBlobStorageService
{
    private static readonly Regex FileTimestampPattern = new(
        @"(?:^|_)(?<date>\d{8})_(?<time>\d{9})(?:\.pdf)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<AzureBlobUploadResult> UploadPdfAsync(string localPath, CancellationToken cancellationToken)
    {
        string connectionString = configuration["AzureBlobStorage:ConnectionString"]
            ?? throw new InvalidOperationException("Falta AzureBlobStorage:ConnectionString.");
        string containerName = configuration["AzureBlobStorage:ContainerName"]
            ?? throw new InvalidOperationException("Falta AzureBlobStorage:ContainerName.");
        string fileName = Path.GetFileName(localPath);
        string blobPath = BuildBlobPath(fileName);
        BlobContainerClient container = new(connectionString, containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        BlobClient blob = container.GetBlobClient(blobPath);

        FileInfo localInfo = new(localPath);
        byte[] localMd5Bytes = await CalculateMd5BytesAsync(localPath, cancellationToken);
        string localMd5 = Convert.ToHexString(localMd5Bytes);

        if (await blob.ExistsAsync(cancellationToken))
        {
            BlobProperties existing = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            string remoteMd5 = existing.ContentHash is { Length: > 0 }
                ? Convert.ToHexString(existing.ContentHash)
                : string.Empty;
            if (existing.ContentLength == localInfo.Length && string.Equals(localMd5, remoteMd5, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Azure Blob: el blob ya existe y coincide por tamaño/MD5: {BlobPath}", blobPath);
                return new AzureBlobUploadResult(blobPath, localInfo.Length, localMd5, remoteMd5, true);
            }
        }

        await using (FileStream stream = File.OpenRead(localPath))
        {
            BlobUploadOptions options = new()
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/pdf",
                    ContentHash = localMd5Bytes
                }
            };
            await blob.UploadAsync(stream, options, cancellationToken);
        }

        BlobProperties uploaded = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        string uploadedMd5 = uploaded.ContentHash is { Length: > 0 }
            ? Convert.ToHexString(uploaded.ContentHash)
            : string.Empty;
        if (uploaded.ContentLength != localInfo.Length || !string.Equals(localMd5, uploadedMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"La verificación del blob falló para {blobPath}.");
        }

        return new AzureBlobUploadResult(blobPath, localInfo.Length, localMd5, uploadedMd5, false);
    }

    private static string BuildBlobPath(string fileName)
    {
        Match match = FileTimestampPattern.Match(fileName);
        if (!match.Success || !DateTime.TryParseExact(
                $"{match.Groups["date"].Value}_{match.Groups["time"].Value}",
                "yyyyMMdd_HHmmssfff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime timestamp))
        {
            throw new InvalidDataException($"El nombre no contiene una fecha válida yyyyMMdd_HHmmssfff: {fileName}");
        }

        return $"{timestamp:yyyy}/{timestamp:MM}/{timestamp:dd}/{timestamp:HH}/{fileName}";
    }

    private static async Task<byte[]> CalculateMd5BytesAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return await MD5.HashDataAsync(stream, cancellationToken);
    }
}
