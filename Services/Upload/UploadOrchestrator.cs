using Replixer.Infrastructure;
using Replixer.Models;
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

    public bool IsTelegramReady => _settings.IsTelegramEnabled && _telegram.IsAuthorized;
    public bool IsKommoEnabled => _kommo.IsEnabled;

    public async Task<UploadResult> UploadAsync(string filePath, string? telegramCaption = null, string? kommoLeadUrl = null, DateTime? callStartTime = null, string? leadSource = null, bool skipTelegram = false, CancellationToken ct = default)
    {
        bool sendTelegram = IsTelegramReady && !skipTelegram;
        if (_settings.IsGoogleDriveEnabled)
        {
            string? folderId = await ResolveTargetFolderAsync(ct);
            string? driveUrl = await _drive.UploadAsync(filePath, folderId, ct);

            int? msgId = sendTelegram
                ? await _telegram.SendFileAsync(filePath, _settings.TelegramChatId, _settings.TelegramTopicId, telegramCaption, driveUrl)
                : null;

            // Post to Kommo before deleting the file
            long? kommoNoteId = null;
            if (!string.IsNullOrWhiteSpace(kommoLeadUrl))
            {
                var kommoBase = CaptionHelper.StripHashtags(telegramCaption ?? string.Empty);
                var kommoNote = string.IsNullOrWhiteSpace(driveUrl)
                    ? kommoBase
                    : kommoBase + $"\n💾 Google Drive: {driveUrl}";
                kommoNoteId = await _kommo.ProcessLeadAsync(kommoLeadUrl, kommoNote, callStartTime, leadSource);
            }

            if (driveUrl is not null)
            {
                SafeDelete(filePath);
                return new UploadResult
                {
                    DriveUrl          = driveUrl.Length > 0 ? driveUrl : null,
                    TelegramMessageId = msgId,
                    TelegramChatId    = _settings.TelegramChatId,
                    TelegramTopicId   = _settings.TelegramTopicId,
                    KommoNoteId       = kommoNoteId,
                };
            }

            Debug.WriteLine("[Upload] Drive upload failed — falling back to local save");
        }
        else
        {
            int? msgId = sendTelegram
                ? await _telegram.SendFileAsync(filePath, _settings.TelegramChatId, _settings.TelegramTopicId, telegramCaption)
                : null;

            long? kommoNoteId = null;
            if (!string.IsNullOrWhiteSpace(kommoLeadUrl))
                kommoNoteId = await _kommo.ProcessLeadAsync(kommoLeadUrl, CaptionHelper.StripHashtags(telegramCaption ?? string.Empty), callStartTime, leadSource);

            return new UploadResult
            {
                LocalPath         = MoveToRecordingsFolder(filePath),
                TelegramMessageId = msgId,
                TelegramChatId    = _settings.TelegramChatId,
                TelegramTopicId   = _settings.TelegramTopicId,
                KommoNoteId       = kommoNoteId,
            };
        }

        return new UploadResult { LocalPath = MoveToRecordingsFolder(filePath) };
    }

    public Task<bool> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null)
        => _telegram.EditMessageAsync(messageId, chatId, topicId, caption, driveUrl);

    public Task<bool> EditKommoNoteAsync(string leadUrl, long noteId, string noteText)
        => _kommo.EditNoteAsync(leadUrl, noteId, noteText);

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
            return null;
        }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
