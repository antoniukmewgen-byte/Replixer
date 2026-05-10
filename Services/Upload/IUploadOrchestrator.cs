namespace Replixer.Services.Upload;

public readonly record struct UploadResult
{
    public string? DriveUrl  { get; init; }
    public string? LocalPath { get; init; }
}

public interface IUploadOrchestrator
{
    Task<UploadResult> UploadAsync(string filePath, CancellationToken ct = default);
}
