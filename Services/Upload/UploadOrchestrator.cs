using Replixer.Models;
using System.Diagnostics;
using System.IO;

namespace Replixer.Services.Upload;

public sealed class UploadOrchestrator : IUploadOrchestrator
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _drive;
    private readonly TelegramUploadService _telegram;

    public UploadOrchestrator(AppSettings settings, GoogleDriveUploadService drive, TelegramUploadService telegram)
    {
        _settings = settings;
        _drive    = drive;
        _telegram = telegram;
    }

    public bool IsTelegramReady => _settings.IsTelegramEnabled && _telegram.IsAuthorized;

    public async Task<UploadResult> UploadAsync(string filePath, string? telegramCaption = null, CancellationToken ct = default)
    {
        // Telegram runs in parallel with Drive upload — both read the file, neither deletes it.
        var telegramTask = IsTelegramReady
            ? _telegram.SendFileAsync(filePath, _settings.TelegramChatId, _settings.TelegramTopicId, telegramCaption)
            : Task.FromResult<int?>(null);

        if (_settings.IsGoogleDriveEnabled)
        {
            string? folderId = await ResolveTargetFolderAsync(ct);
            string? driveUrl = await _drive.UploadAsync(filePath, folderId, ct);

            int? msgId = await telegramTask;

            if (driveUrl is not null)
            {
                SafeDelete(filePath);
                return new UploadResult
                {
                    DriveUrl          = driveUrl.Length > 0 ? driveUrl : null,
                    TelegramMessageId = msgId,
                    TelegramChatId    = _settings.TelegramChatId,
                    TelegramTopicId   = _settings.TelegramTopicId,
                };
            }

            Debug.WriteLine("[Upload] Drive upload failed — falling back to local save");
        }
        else
        {
            int? msgId = await telegramTask;
            return new UploadResult
            {
                LocalPath         = MoveToRecordingsFolder(filePath),
                TelegramMessageId = msgId,
                TelegramChatId    = _settings.TelegramChatId,
                TelegramTopicId   = _settings.TelegramTopicId,
            };
        }

        return new UploadResult { LocalPath = MoveToRecordingsFolder(filePath) };
    }

    public Task<bool> EditTelegramCaptionAsync(int messageId, long chatId, int? topicId, string caption)
        => _telegram.EditMessageAsync(messageId, chatId, topicId, caption);

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
