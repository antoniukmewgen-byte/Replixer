namespace Replixer.Services.Upload;

public readonly record struct UploadResult
{
    public string? DriveUrl          { get; init; }
    public string? LocalPath         { get; init; }
    public int?    TelegramMessageId { get; init; }
    public long    TelegramChatId    { get; init; }
    public int?    TelegramTopicId   { get; init; }
}

public interface IUploadOrchestrator
{
    bool IsTelegramReady { get; }
    Task<UploadResult> UploadAsync(string filePath, string? telegramCaption = null, CancellationToken ct = default);
    Task<bool> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption);
}
