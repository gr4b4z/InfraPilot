namespace Platform.Api.Features.Requests.Models;

public class FileAttachment
{
    public Guid Id { get; set; }
    public Guid ServiceRequestId { get; set; }
    public string InputId { get; set; } = "";
    public string Filename { get; set; } = "";
    public string BlobReference { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    public ServiceRequest? ServiceRequest { get; set; }
}
