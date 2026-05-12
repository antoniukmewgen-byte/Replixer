namespace Replixer.Services.Upload;

public readonly record struct UploadResult
{
    public string? DriveUrl  { get; init; }
    public string? LocalPath { get; init; }
}

public interface IUploadOrchestrator
{
    bool IsTelegramReady { get; }
    Task<UploadResult> UploadAsync(string filePath, string? telegramCaption = null, CancellationToken ct = default);
}
