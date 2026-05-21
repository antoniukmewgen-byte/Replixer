namespace Replixer.Services.Upload;

public readonly record struct UploadResult
{
    public string? DriveUrl          { get; init; }
    public string? LocalPath         { get; init; }
    public int?    TelegramMessageId { get; init; }
    public long    TelegramChatId    { get; init; }
    public int?    TelegramTopicId   { get; init; }
    public long?   KommoNoteId       { get; init; }
}

public interface IUploadOrchestrator
{
    bool IsTelegramReady { get; }
    bool IsKommoEnabled  { get; }
    Task<UploadResult> UploadAsync(string filePath, string? telegramCaption = null, string? kommoLeadUrl = null, DateTime? callStartTime = null, CancellationToken ct = default);
    Task<bool> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null);
    Task<bool> EditKommoNoteAsync(string leadUrl, long noteId, string noteText);
}
