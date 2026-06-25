using Replixer.Infrastructure;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Replixer.ViewModels.Dialogs;

public class CallDialogViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer? _timer;
    private readonly string _originalMessage;
    private string _recordingDuration = "00:00:00";
    private bool _isConfirming;

    public string      AppName           { get; }
    public ImageSource AppIconSource     { get; }
    public Brush       AppBackground     { get; }
    public Brush       PrimaryBackground { get; }
    public Brush       PrimaryAccent     { get; }
    public string      StatusText        { get; }
    public Brush       StatusColor       { get; }
    public string      PrimaryLabel      { get; }
    public string      SecondaryLabel    { get; }
    public ICommand    PrimaryCommand    { get; }
    public ICommand    SecondaryCommand  { get; }

    public bool IsConfirming
    {
        get => _isConfirming;
        private set
        {
            if (SetField(ref _isConfirming, value))
                OnPropertyChanged(nameof(DisplayMessage));
        }
    }

    public string DisplayMessage => _isConfirming
        ? "Ви впевнені що хочете відмінити?"
        : _originalMessage;

    public string RecordingDuration
    {
        get => _recordingDuration;
        private set => SetField(ref _recordingDuration, value);
    }

    public CallDialogViewModel(
        string appName,
        string message,
        string primaryLabel,   Action onPrimary,
        string secondaryLabel, Action onSecondary,
        DateTime? recordingStartedAt = null,
        bool confirmSecondary = false)
    {
        _originalMessage = message;

        AppName       = PlatformHelper.ToDisplayName(appName);
        AppIconSource = new BitmapImage(new Uri(PlatformHelper.ToIconUri(appName)));
        AppBackground = (Brush)System.Windows.Application.Current.Resources[PlatformHelper.ToBrushKey(appName)];

        bool isStop       = recordingStartedAt.HasValue;
        PrimaryBackground = new SolidColorBrush(isStop ? Color.FromRgb(0x2D, 0x1A, 0x1A) : Color.FromRgb(0x19, 0x2D, 0x28));
        PrimaryAccent     = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        StatusText        = isStop ? "Завершився" : "Активний";
        StatusColor       = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        PrimaryLabel  = primaryLabel;
        SecondaryLabel = secondaryLabel;

        PrimaryCommand = new RelayCommand(() =>
        {
            if (_disposed) return;
            if (_isConfirming) { IsConfirming = false; return; }
            Dispose();
            onPrimary();
        });

        SecondaryCommand = new RelayCommand(() =>
        {
            if (_disposed) return;
            if (confirmSecondary && !_isConfirming) { IsConfirming = true; return; }
            Dispose();
            onSecondary();
        });

        if (recordingStartedAt.HasValue)
        {
            var startedAt = recordingStartedAt.Value;
            UpdateDuration(startedAt);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateDuration(startedAt);
            _timer.Start();
        }
    }

    private void UpdateDuration(DateTime startedAt)
    {
        var elapsed = DateTime.Now - startedAt;
        RecordingDuration = elapsed.ToString(@"hh\:mm\:ss");
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _timer?.Stop();
    }
}
