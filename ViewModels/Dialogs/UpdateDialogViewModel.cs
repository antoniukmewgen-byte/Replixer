using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Replixer.Infrastructure;
using Replixer.Services;

namespace Replixer.ViewModels.Dialogs;

public sealed class UpdateDialogViewModel : ViewModelBase
{
    private readonly UpdateService _updateService;
    private readonly UpdateInfo    _info;

    public string NewVersion => _info.NewVersion.ToString(3);

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetField(ref _isDownloading, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            SetField(ref _downloadProgress, value);
            OnPropertyChanged(nameof(DownloadProgressText));
        }
    }

    public string DownloadProgressText => $"{_downloadProgress * 100:0}%";

    // Заповнюється лише коли всі внутрішні ретраї DownloadUpdatesAsync вичерпані і виняток
    // таки долетів сюди — раніше в цьому випадку діалог просто "завмирав" (прогрес-бар зникав
    // разом з IsDownloading, а самого повідомлення чи способу повторити спробу в UI не було,
    // менеджер лишався зі статичним вікном без жодної дії, крім перезапуску додатку).
    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            SetField(ref _errorMessage, value);
            OnPropertyChanged(nameof(HasFailed));
        }
    }

    public bool HasFailed => _errorMessage is not null;

    public ICommand RetryCommand { get; }

    public UpdateDialogViewModel(UpdateService updateService, UpdateInfo info)
    {
        _updateService = updateService;
        _info          = info;
        RetryCommand   = new RelayCommand(() => _ = DownloadAndInstallAsync());

        _ = DownloadAndInstallAsync();
    }

    private async Task DownloadAndInstallAsync()
    {
        ErrorMessage     = null;
        DownloadProgress = 0;
        IsDownloading    = true;
        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var progress   = new Progress<double>(p =>
                dispatcher.BeginInvoke(() => DownloadProgress = p));

            var stagingDir = await _updateService.DownloadUpdatesAsync(_info, progress);
            _updateService.LaunchUpdaterAndExit(stagingDir, _info.NewVersion);
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            ErrorMessage  = "Не вдалося завантажити оновлення. Перевірте з'єднання й спробуйте ще раз.";
            ErrorReporter.Report("UPDATE", $"Помилка завантаження оновлення до v{_info.NewVersion}: {ex.Message}", ex);
        }
    }
}
