namespace Replixer.Services.Upload;

public readonly record struct UploadResult
{
    public string? DriveUrl          { get; init; }
    public string? LocalPath         { get; init; }
    public int?    TelegramMessageId { get; init; }
    public long    TelegramChatId    { get; init; }
    public int?    TelegramTopicId   { get; init; }
    public long?   KommoNoteId       { get; init; }

    public bool    TelegramAttempted { get; init; }
    public string? TelegramWarning   { get; init; }

    public bool    KommoAttempted    { get; init; }
    public string? KommoWarning      { get; init; }

    public string? DriveWarning       { get; init; }
    public string? LocalPathWarning   { get; init; }
}

public interface IUploadOrchestrator
{
    bool IsTelegramReady { get; }
    bool IsKommoEnabled  { get; }

    Task<UploadResult> UploadAsync(
        string filePath,
        string? telegramCaption = null,
        string? kommoLeadUrl    = null,
        DateTime? callStartTime = null,
        string? leadSource      = null,
        bool skipTelegram       = false,
        string? callType        = null,
        CancellationToken ct    = default);

    Task<string?> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null);

    Task<string?> EditKommoNoteAsync(string leadUrl, long noteId, string noteText, string? callType = null);
}
