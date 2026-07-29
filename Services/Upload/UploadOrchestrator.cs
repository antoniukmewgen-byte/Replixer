using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using System.Diagnostics;
using System.IO;

namespace Replixer.Services.Upload;

public sealed class UploadOrchestrator : IUploadOrchestrator
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _drive;
    private readonly TelegramUploadService _telegram;
    private readonly KommoService _kommo;

    public UploadOrchestrator(AppSettings settings, GoogleDriveUploadService drive, TelegramUploadService telegram, KommoService kommo)
    {
        _settings = settings;
        _drive    = drive;
        _telegram = telegram;
        _kommo    = kommo;
    }

    public bool IsTelegramReady => _settings.IsTelegramEnabled && (_telegram.IsReady || _telegram.IsAuthorized);
    public bool IsKommoEnabled  => _kommo.IsEnabled;

    public async Task<UploadResult> UploadAsync(
        string filePath,
        string? telegramCaption = null,
        string? kommoLeadUrl    = null,
        DateTime? callStartTime = null,
        string? leadSource      = null,
        bool skipTelegram       = false,
        string? callType        = null,
        CancellationToken ct    = default)
    {
        bool sendTelegram = IsTelegramReady && !skipTelegram;

        if (_settings.IsGoogleDriveEnabled)
        {
            string? folderId = await ResolveTargetFolderAsync(ct);
            string? driveUrl = await _drive.UploadAsync(filePath, folderId, ct: ct);

            var (tgMessageId, tgWarning) = await SendTelegramAsync(
                sendTelegram, filePath, telegramCaption, driveUrl);

            var (kommoNoteId, kommoWarning, _, _) = await PostKommoAsync(
                kommoLeadUrl, telegramCaption, driveUrl, callStartTime, leadSource, callType);

            // Файл видаляємо ЛИШЕ якщо геть усі кроки, які мали відбутись, реально
            // завершились успішно. Раніше видалення залежало тільки від driveUrl —
            // якщо Диск проходив, а Telegram чи Kommo впадали по мережі, файл
            // видалявся назавжди, і фоновому retry вже не було чим користуватись.
            bool driveOk    = driveUrl is not null;
            bool telegramOk = tgWarning is null;
            bool kommoOk    = kommoWarning is null;

            if (driveOk && telegramOk && kommoOk)
            {
                SafeDelete(filePath);
                return new UploadResult
                {
                    DriveUrl          = driveUrl!.Length > 0 ? driveUrl : null,
                    TelegramMessageId = tgMessageId,
                    TelegramChatId    = _settings.TelegramChatId,
                    TelegramTopicId   = _settings.TelegramTopicId,
                    TelegramAttempted = sendTelegram,
                    TelegramWarning   = tgWarning,
                    KommoNoteId       = kommoNoteId,
                    KommoAttempted    = !string.IsNullOrWhiteSpace(kommoLeadUrl) && _kommo.IsEnabled,
                    KommoWarning      = kommoWarning,
                };
            }

            Debug.WriteLine("[Upload] Не всі кроки вдалися — файл лишається на диску для фонового retry");
            return new UploadResult
            {
                DriveUrl          = driveUrl is { Length: > 0 } ? driveUrl : null,
                DriveWarning      = driveOk ? null : "Google Drive: не вдалося завантажити файл",
                TelegramMessageId = tgMessageId,
                TelegramChatId    = _settings.TelegramChatId,
                TelegramTopicId   = _settings.TelegramTopicId,
                TelegramAttempted = sendTelegram,
                TelegramWarning   = tgWarning,
                KommoNoteId       = kommoNoteId,
                KommoAttempted    = !string.IsNullOrWhiteSpace(kommoLeadUrl) && _kommo.IsEnabled,
                KommoWarning      = kommoWarning,
            };
        }

        var (tgMsgId, tgWarn) = await SendTelegramAsync(
            sendTelegram, filePath, telegramCaption, driveUrl: null);

        var (kommoId, kommoWarn, _, _) = await PostKommoAsync(
            kommoLeadUrl, telegramCaption, driveUrl: null, callStartTime, leadSource, callType);

        var localPath = MoveToRecordingsFolder(filePath);

        return new UploadResult
        {
            LocalPath         = localPath,
            LocalPathWarning  = localPath is null ? "Не вдалося зберегти файл у папку записів" : null,
            TelegramMessageId = tgMsgId,
            TelegramChatId    = _settings.TelegramChatId,
            TelegramTopicId   = _settings.TelegramTopicId,
            TelegramAttempted = sendTelegram,
            TelegramWarning   = tgWarn,
            KommoNoteId       = kommoId,
            KommoAttempted    = !string.IsNullOrWhiteSpace(kommoLeadUrl) && _kommo.IsEnabled,
            KommoWarning      = kommoWarn,
        };
    }

    // Тихий фоновий "добір" лише тих кроків, які раніше не вдалися (мережева помилка тощо).
    // Кроки, що вже мають значення (existingDriveUrl/existingTelegramMessageId/existingKommoNoteId),
    // НЕ повторюються — інакше в Kommo задублювалась би нотатка, а в Telegram — повідомлення.
    // needX прапорці кажуть, які кроки взагалі мали відбутись (беруться з RecordingEntry.*Failed).
    public async Task<UploadResult> RetryMissingStepsAsync(
        string filePath,
        string? existingDriveUrl,
        int? existingTelegramMessageId,
        long? existingKommoNoteId,
        bool needDrive,
        bool needTelegram,
        bool needKommo,
        string? telegramCaption,
        string? kommoLeadUrl,
        DateTime? callStartTime,
        string? leadSource,
        string? callType,
        CancellationToken ct = default)
    {
        string? driveUrl = existingDriveUrl;
        if (needDrive && driveUrl is null)
        {
            string? folderId = await ResolveTargetFolderAsync(ct);
            driveUrl = await _drive.UploadAsync(filePath, folderId, ct: ct);
        }

        int?    tgMessageId = existingTelegramMessageId;
        string? tgWarning   = null;
        if (needTelegram && tgMessageId is null)
            (tgMessageId, tgWarning) = await SendTelegramAsync(true, filePath, telegramCaption, driveUrl);

        long?   kommoNoteId  = existingKommoNoteId;
        string? kommoWarning = null;
        if (needKommo && kommoNoteId is null)
            (kommoNoteId, kommoWarning, _, _) = await PostKommoAsync(kommoLeadUrl, telegramCaption, driveUrl, callStartTime, leadSource, callType);

        return new UploadResult
        {
            DriveUrl          = driveUrl,
            DriveWarning      = needDrive && driveUrl is null ? "Google Drive: не вдалося завантажити файл" : null,
            TelegramMessageId = tgMessageId,
            TelegramChatId    = _settings.TelegramChatId,
            TelegramTopicId   = _settings.TelegramTopicId,
            TelegramAttempted = needTelegram,
            TelegramWarning   = tgWarning,
            KommoNoteId       = kommoNoteId,
            KommoAttempted    = needKommo,
            KommoWarning      = kommoWarning,
        };
    }

    public Task<string?> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null)
        => _telegram.EditMessageAsync(messageId, chatId, topicId, caption, driveUrl);

    public Task<string?> EditKommoNoteAsync(string leadUrl, long noteId, string noteText, string? callType = null)
        => _kommo.EditNoteAsync(leadUrl, noteId, noteText, callType);

    public async Task<KommoNoteDeliveryResult> PostKommoNoteAsync(string kommoLeadUrl, string note, DateTime? callStartTime, string? callType = null)
    {
        var (_, warning, speedMinutes, speedWorkMinutes) = await PostKommoAsync(kommoLeadUrl, note, driveUrl: null, callStartTime, leadSource: null, callType);
        return new KommoNoteDeliveryResult(warning, speedMinutes, speedWorkMinutes);
    }

    public Task<(int? ProcessingSpeedMinutes, int? ProcessingSpeedWorkMinutes)> RecalculateProcessingSpeedAsync(string kommoLeadUrl, DateTime callStartTime)
        => _kommo.RecalculateProcessingSpeedAsync(kommoLeadUrl, callStartTime);

    private async Task<(int? MessageId, string? Warning)> SendTelegramAsync(
        bool send, string filePath, string? caption, string? driveUrl)
    {
        if (!send) return (null, null);

        int? msgId = await _telegram.SendFileAsync(
            filePath, _settings.TelegramChatId, _settings.TelegramTopicId, caption, driveUrl);

        string? warning = msgId is null ? "Telegram: не вдалося відправити файл" : null;
        return (msgId, warning);
    }

    private async Task<(long? NoteId, string? Warning, int? SpeedMinutes, int? SpeedWorkMinutes)> PostKommoAsync(
        string? kommoLeadUrl, string? telegramCaption, string? driveUrl,
        DateTime? callStartTime, string? leadSource, string? callType = null)
    {
        if (string.IsNullOrWhiteSpace(kommoLeadUrl)) return (null, null, null, null);

        var kommoBase = CaptionHelper.StripHashtags(telegramCaption ?? string.Empty);
        var kommoNote = string.IsNullOrWhiteSpace(driveUrl)
            ? kommoBase
            : kommoBase + $"\n💾 Google Drive: {driveUrl}";
        kommoNote += $"\n🔖 v{ErrorReporter.AppVersion}";

        var (noteId, speedMinutes, speedWorkMinutes) = await _kommo.ProcessLeadAsync(kommoLeadUrl, kommoNote, callStartTime, callType);

        string? warning = noteId is null && _kommo.IsEnabled
            ? "Kommo: не вдалося створити нотатку"
            : null;

        return (noteId, warning, speedMinutes, speedWorkMinutes);
    }

    private async Task<string?> ResolveTargetFolderAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserFolderId))
            return _settings.UserFolderId;

        if (!string.IsNullOrWhiteSpace(_settings.UserFolderName) &&
            !string.IsNullOrWhiteSpace(_settings.GoogleDriveFolderId))
        {
            var id = await _drive.GetOrCreateUserFolderAsync(
                _settings.GoogleDriveFolderId, _settings.UserFolderName, ct);
            if (id is not null)
            {
                _settings.UserFolderId = id;
                return id;
            }
        }

        return string.IsNullOrWhiteSpace(_settings.GoogleDriveFolderId)
            ? null
            : _settings.GoogleDriveFolderId;
    }

    private string? MoveToRecordingsFolder(string tempPath)
    {
        try
        {
            Directory.CreateDirectory(_settings.RecordingsFolder);
            string dest = Path.Combine(_settings.RecordingsFolder, Path.GetFileName(tempPath));
            File.Move(tempPath, dest, overwrite: true);
            Debug.WriteLine($"[Upload] Saved locally → {dest}");
            return dest;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Upload] Local save failed: {ex.Message}");
            ErrorReporter.Report("UPLOAD", $"Не вдалося перемістити запис у папку збереження: {ex.Message}", ex);
            return null;
        }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
