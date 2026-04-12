namespace Platform.Api.Infrastructure.FileStorage;

public interface IFileStorage
{
    Task<string> Upload(string fileName, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> Download(string blobReference, CancellationToken ct = default);
    Task Delete(string blobReference, CancellationToken ct = default);
}
