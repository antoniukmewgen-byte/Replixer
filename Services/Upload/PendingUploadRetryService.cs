using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.ViewModels;
using Replixer.ViewModels.Dialogs;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Replixer.Services.Upload;

/// <summary>
/// Фоновий "добір" записів, у яких попередня спроба відправки в Telegram/Kommo/Google Drive
/// впала (як правило — через мережеву проблему). Раз на 10 секунд перевіряє список записів
/// (той самий, що вже персистується в recordings.json — тож переживає навіть перезапуск
/// додатку) і тихо, без жодних діалогів чи участі користувача, повторює лише ті кроки,
/// які не вдалися. Кроки, що вже пройшли, НІКОЛИ не повторюються — інакше в CRM/Telegram
/// задублювалась би нотатка/повідомлення.
///
/// Кнопка "Редагувати" в інтерфейсі жодних додаткових змін не потребує — вона вже прив'язана
/// до RecordingEntry.TelegramMessageId (через CommandManager.RequerySuggested), тож щойно ми
/// проставимо це поле після успішного фонового retry, кнопка сама стане активною.
/// </summary>
public sealed class PendingUploadRetryService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly RecordingsViewModel _recordingsVm;
    private readonly IUploadOrchestrator _orchestrator;
    private readonly AppSettings _settings;

    private Timer? _timer;
    private int _isTicking; // 0/1-guard: не даємо двом тікам накладатись, якщо мережа повільна

    public PendingUploadRetryService(RecordingsViewModel recordingsVm, IUploadOrchestrator orchestrator, AppSettings settings)
    {
        _recordingsVm = recordingsVm;
        _orchestrator = orchestrator;
        _settings     = settings;
    }

    public void Start()
    {
        if (_timer is not null) return; // вже запущено
        _timer = new Timer(_ => _ = TickAsync(), null, Interval, Interval);
        Debug.WriteLine("[PendingRetry] Фоновий сервіс відновлення записів запущено (кожні 10с)");
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _isTicking, 1) == 1) return;
        try
        {
            // Знімок кандидатів + позначення "в роботі" робимо одним атомарним кроком на UI-потоці,
            // щоб виключити перегони з ручним Retry/новим записом за той самий entry.
            var candidates = await RunOnUiThreadAsync(() =>
            {
                var list = _recordingsVm.Recordings
                    .Where(e => e.NeedsBackgroundRetry && !e.IsBackgroundRetrying)
                    .ToList();

                foreach (var e in list) e.IsBackgroundRetrying = true;
                return list;
            });

            foreach (var entry in candidates)
                await RetryEntryAsync(entry);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("PENDING_UPLOAD_RETRY", "Помилка фонового відновлення записів.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isTicking, 0);
        }
    }

    private async Task RetryEntryAsync(RecordingEntry entry)
    {
        try
        {
            var filePath = (!string.IsNullOrEmpty(entry.SourcePath) && File.Exists(entry.SourcePath)) ? entry.SourcePath
                         : (!string.IsNullOrEmpty(entry.FilePath)   && File.Exists(entry.FilePath))   ? entry.FilePath
                         : null;

            var reportData = entry.ReportData;

            if (filePath is null || reportData is null)
            {
                // Файл фізично зник (користувач видалив вручну тощо) — тримати прапорці
                // "спробувати ще" немає сенсу, це лише нескінченно ганяло б порожні спроби.
                await RunOnUiThreadAsync(() =>
                {
                    if (filePath is null)
                    {
                        entry.DriveFailed    = false;
                        entry.TelegramFailed = false;
                        entry.KommoFailed    = false;
                    }
                });
                return;
            }

            var caption  = reportData.FormatCaption();
            var callType = ResolveCallType(reportData);

            var upload = await _orchestrator.RetryMissingStepsAsync(
                filePath,
                existingDriveUrl:          entry.DriveUrl,
                existingTelegramMessageId: entry.TelegramMessageId,
                existingKommoNoteId:       entry.KommoNoteId,
                needDrive:    entry.DriveFailed,
                needTelegram: entry.TelegramFailed,
                needKommo:    entry.KommoFailed,
                telegramCaption: caption,
                kommoLeadUrl:    reportData.CrmUrl,
                callStartTime:   entry.StartedAt,
                leadSource:      reportData.LeadSource,
                callType:        callType);

            await RunOnUiThreadAsync(() =>
            {
                if (upload.DriveUrl is not null)          entry.DriveUrl          = upload.DriveUrl;
                if (upload.TelegramMessageId is not null) entry.TelegramMessageId = upload.TelegramMessageId;
                if (upload.TelegramChatId != 0)           entry.TelegramChatId    = upload.TelegramChatId;
                if (upload.TelegramTopicId is not null)   entry.TelegramTopicId   = upload.TelegramTopicId;
                if (upload.KommoNoteId is not null)       entry.KommoNoteId       = upload.KommoNoteId;

                entry.DriveFailed    = entry.DriveFailed    && upload.DriveWarning    is not null;
                entry.TelegramFailed = entry.TelegramFailed && upload.TelegramWarning is not null;
                entry.KommoFailed    = entry.KommoFailed    && upload.KommoWarning    is not null;

                if (!entry.DriveFailed && !entry.TelegramFailed && !entry.KommoFailed)
                {
                    if (entry.Status == RecordingStatus.Error)
                        entry.Status = RecordingStatus.Saved;

                    // Усе доставлено — якщо Диск є основним сховищем, локальна копія більше не потрібна.
                    if (_settings.IsGoogleDriveEnabled && entry.DriveUrl is not null)
                        SafeDeleteFile(filePath);

                    Debug.WriteLine($"[PendingRetry] Запис {entry.StartedAt:dd.MM HH:mm} успішно доджато у фоні.");
                    NotificationService.ShowSuccess("Запис, який раніше не вдалося відправити, тепер успішно доставлено.");
                }
            });
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("PENDING_UPLOAD_RETRY", $"Не вдалося відновити запис ({entry.StartedAt:dd.MM HH:mm}).", ex);
        }
        finally
        {
            await RunOnUiThreadAsync(() => entry.IsBackgroundRetrying = false);
        }
    }

    private static string? ResolveCallType(CallReportData reportData)
    {
        if (string.IsNullOrEmpty(reportData.CallType)) return null;
        return reportData.CallType == "Інший" && !string.IsNullOrWhiteSpace(reportData.CustomCallType)
            ? reportData.CustomCallType
            : reportData.CallType;
    }

    private static void SafeDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* не критично — спробуємо ще раз наступного разу, якщо знадобиться */ }
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return Task.FromResult(func());

        return dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
