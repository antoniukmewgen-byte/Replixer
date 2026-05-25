using Replixer.Infrastructure;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Replixer.ViewModels.Dialogs;

public class CallDialogViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer? _timer;
    private string _recordingDuration = "00:00:00";

    public string      AppName           { get; }
    public ImageSource AppIconSource     { get; }
    public Brush       AppBackground     { get; }
    public Brush       PrimaryBackground { get; }
    public Brush       PrimaryAccent     { get; }
    public string      StatusText        { get; }
    public Brush       StatusColor       { get; }
    public string      Message           { get; }
    public string      PrimaryLabel      { get; }
    public string      SecondaryLabel    { get; }
    public ICommand    PrimaryCommand    { get; }
    public ICommand    SecondaryCommand  { get; }

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
        DateTime? recordingStartedAt = null)
    {
        AppName       = PlatformHelper.ToDisplayName(appName);
        AppIconSource = new BitmapImage(new Uri(PlatformHelper.ToIconUri(appName)));
        AppBackground = (Brush)System.Windows.Application.Current.Resources[PlatformHelper.ToBrushKey(appName)];

        bool isStop       = recordingStartedAt.HasValue;
        PrimaryBackground = new SolidColorBrush(isStop ? Color.FromRgb(0x2D, 0x1A, 0x1A) : Color.FromRgb(0x19, 0x2D, 0x28));
        PrimaryAccent     = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        StatusText        = isStop ? "Завершився" : "Активний";
        StatusColor       = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        Message       = message;
        PrimaryLabel  = primaryLabel;
        SecondaryLabel = secondaryLabel;

        PrimaryCommand   = new RelayCommand(() => { Dispose(); onPrimary(); });
        SecondaryCommand = new RelayCommand(() => { Dispose(); onSecondary(); });

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

    public void Dispose() => _timer?.Stop();
}
