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
        private set => SetField(ref _downloadProgress, value);
    }

    public AsyncRelayCommand UpdateNowCommand { get; }
    public RelayCommand      DismissCommand   { get; }

    public UpdateDialogViewModel(UpdateService updateService, UpdateInfo info,
        Action onDismiss)
    {
        _updateService = updateService;
        _info          = info;

        UpdateNowCommand = new AsyncRelayCommand(DownloadAndInstallAsync);
        DismissCommand   = new RelayCommand(onDismiss);
    }

    private async Task DownloadAndInstallAsync()
    {
        IsDownloading = true;
        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var progress = new Progress<double>(p =>
                dispatcher.BeginInvoke(() => DownloadProgress = p));

            var path = await _updateService.DownloadInstallerAsync(_info, progress);
            _updateService.LaunchInstallerAndExit(path);
        }
        catch
        {
            IsDownloading = false;
        }
    }
}
