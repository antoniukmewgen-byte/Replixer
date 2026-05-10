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

    public async Task<UploadResult> UploadAsync(string filePath, CancellationToken ct = default)
    {
        // Telegram runs in parallel with Drive upload — both read the file, neither deletes it.
        var telegramTask = (_settings.IsTelegramEnabled && _telegram.IsAuthorized)
            ? _telegram.SendFileAsync(filePath, _settings.TelegramChatId)
            : Task.FromResult(false);

        if (_settings.IsGoogleDriveEnabled)
        {
            string? folderId = ResolveTargetFolder();
            string? driveUrl = await _drive.UploadAsync(filePath, folderId, ct);

            await telegramTask;

            if (driveUrl is not null)
            {
                SafeDelete(filePath);
                return new UploadResult { DriveUrl = driveUrl.Length > 0 ? driveUrl : null };
            }

            Debug.WriteLine("[Upload] Drive upload failed — falling back to local save");
        }
        else
        {
            await telegramTask;
        }

        return new UploadResult { LocalPath = MoveToRecordingsFolder(filePath) };
    }

    private string? ResolveTargetFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserFolderId))
            return _settings.UserFolderId;
        if (!string.IsNullOrWhiteSpace(_settings.GoogleDriveFolderId))
            return _settings.GoogleDriveFolderId;
        return null;
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
