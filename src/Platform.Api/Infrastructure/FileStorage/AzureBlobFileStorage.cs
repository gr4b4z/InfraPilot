using Azure.Storage.Blobs;

namespace Platform.Api.Infrastructure.FileStorage;

public class AzureBlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;

    public AzureBlobFileStorage(IConfiguration config)
    {
        var client = new BlobServiceClient(config["AzureBlob:ConnectionString"]);
        _container = client.GetBlobContainerClient(config["AzureBlob:ContainerName"] ?? "request-attachments");
    }

    public async Task<string> Upload(string fileName, Stream content, string contentType, CancellationToken ct = default)
    {
        var blobName = $"{Guid.NewGuid()}/{fileName}";
        var blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        return blobName;
    }

    public async Task<Stream> Download(string blobReference, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobReference);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task Delete(string blobReference, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(blobReference);
        await blob.DeleteIfExistsAsync(cancellationToken: ct);
    }
}
