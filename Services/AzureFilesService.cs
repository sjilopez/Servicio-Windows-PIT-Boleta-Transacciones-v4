using System.Security.Cryptography;
using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;

namespace PITBoletaTransacciones.Services;

public interface IAzureFilesService
{
    Task<AzureFileCopyResult> CopyAsync(string localPath, string remotePath, CancellationToken cancellationToken);
}

public sealed record AzureFileCopyResult(
    string RemotePath,
    long Length,
    string LocalMd5,
    string RemoteMd5,
    bool AlreadyExisted);

public sealed class AzureFilesService(IConfiguration configuration, ILogger<AzureFilesService> logger) : IAzureFilesService
{
    public async Task<AzureFileCopyResult> CopyAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        string connectionString = configuration["AzureFiles:ConnectionString"]
            ?? throw new InvalidOperationException("Falta AzureFiles:ConnectionString.");
        string shareName = configuration["AzureFiles:ShareName"]
            ?? throw new InvalidOperationException("Falta AzureFiles:ShareName.");

        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("No existe el archivo local para copiar a Azure Files.", localPath);
        }

        ShareClient share = new(connectionString, shareName);
        await share.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        string normalizedPath = remotePath.Replace("\\", "/").Trim('/');
        string[] parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidOperationException("La ruta remota de Azure Files está vacía.");

        ShareDirectoryClient directory = share.GetRootDirectoryClient();
        for (int index = 0; index < parts.Length - 1; index++)
        {
            directory = directory.GetSubdirectoryClient(parts[index]);
            await directory.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        ShareFileClient remoteFile = directory.GetFileClient(parts[^1]);
        FileInfo localInfo = new(localPath);
        string localMd5 = await CalculateMd5Async(localPath, cancellationToken);

        try
        {
            await remoteFile.CreateAsync(localInfo.Length, cancellationToken: cancellationToken);
            await using FileStream stream = File.OpenRead(localPath);
            await remoteFile.UploadRangeAsync(new HttpRange(0, stream.Length), stream, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            ShareFileProperties existing = await remoteFile.GetPropertiesAsync(cancellationToken: cancellationToken);
            string remoteMd5 = existing.ContentHash is { Length: > 0 }
                ? Convert.ToHexString(existing.ContentHash)
                : await DownloadMd5Async(remoteFile, existing.ContentLength, cancellationToken);

            if (existing.ContentLength != localInfo.Length || !string.Equals(localMd5, remoteMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Ya existe un archivo diferente en Azure Files: {remotePath}");
            }

            logger.LogInformation("Azure Files: el archivo remoto ya existe y coincide por tamaño/MD5: {RemotePath}", remotePath);
            return new AzureFileCopyResult(remotePath, localInfo.Length, localMd5, remoteMd5, true);
        }

        ShareFileProperties uploaded = await remoteFile.GetPropertiesAsync(cancellationToken: cancellationToken);
        string uploadedMd5 = uploaded.ContentHash is { Length: > 0 }
            ? Convert.ToHexString(uploaded.ContentHash)
            : await DownloadMd5Async(remoteFile, uploaded.ContentLength, cancellationToken);
        if (uploaded.ContentLength != localInfo.Length || !string.Equals(localMd5, uploadedMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"La verificación de integridad falló para Azure Files: {remotePath}");
        }

        return new AzureFileCopyResult(remotePath, localInfo.Length, localMd5, uploadedMd5, false);
    }

    private static async Task<string> DownloadMd5Async(ShareFileClient remoteFile, long length, CancellationToken cancellationToken)
    {
        Response<ShareFileDownloadInfo> download = await remoteFile.DownloadAsync(cancellationToken: cancellationToken);
        using Stream stream = download.Value.Content;
        return Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<string> CalculateMd5Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken));
    }
}
