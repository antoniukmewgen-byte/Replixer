using EchoVault.Infrastructure;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EchoVault.ViewModels.Dialogs;

public class CallDialogViewModel : ViewModelBase
{
    private readonly DispatcherTimer? _timer;
    private string _recordingDuration = "00:00:00";

    public string AppName { get; }
    public ImageSource AppIconSource { get; }
    public Brush AppBackground { get; }
    public Brush PrimaryBackground { get; }
    public Brush PrimaryAccent { get; }
    public string StatusText { get; }
    public Brush StatusColor { get; }
    public string Message { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }

    public string RecordingDuration
    {
        get => _recordingDuration;
        private set => SetField(ref _recordingDuration, value);
    }

    public CallDialogViewModel(
        string appName,
        string message,
        string primaryLabel, Action onPrimary,
        string secondaryLabel, Action onSecondary,
        DateTime? recordingStartedAt = null)
    {
        AppName = ResolveDisplayName(appName);
        AppIconSource = ResolveIcon(appName);
        AppBackground = ResolveBrush(appName);
        bool isStop = recordingStartedAt.HasValue;
        PrimaryBackground = new SolidColorBrush(isStop ? Color.FromRgb(0x2D, 0x1A, 0x1A) : Color.FromRgb(0x19, 0x2D, 0x28));
        PrimaryAccent     = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        StatusText  = isStop ? "Завершився" : "Активний";
        StatusColor = new SolidColorBrush(isStop ? Color.FromRgb(0xC6, 0x44, 0x44) : Color.FromRgb(0x22, 0xC6, 0x79));
        Message = message;
        PrimaryLabel = primaryLabel;
        SecondaryLabel = secondaryLabel;

        PrimaryCommand = new RelayCommand(() => { StopTimer(); onPrimary(); });
        SecondaryCommand = new RelayCommand(() => { StopTimer(); onSecondary(); });

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

    private void StopTimer() => _timer?.Stop();

    private static string ResolveDisplayName(string processName) =>
        processName.ToLowerInvariant() switch
        {
            "viber" => "Viber",
            var s when s.Contains("whatsapp") => "WhatsApp",
            _ => "Telegram"
        };

    private static ImageSource ResolveIcon(string processName) =>
        new BitmapImage(new Uri(processName.ToLowerInvariant() switch
        {
            "viber" => "pack://application:,,,/Assets/Icons/viber.png",
            var s when s.Contains("whatsapp") => "pack://application:,,,/Assets/Icons/whatsapp.png",
            _ => "pack://application:,,,/Assets/Icons/telegram.png"
        }));

    private static Brush ResolveBrush(string processName) =>
        processName.ToLowerInvariant() switch
        {
            "viber" => new SolidColorBrush(Color.FromRgb(0x3A, 0x1A, 0x4A)),
            var s when s.Contains("whatsapp") => new SolidColorBrush(Color.FromRgb(0x1B, 0x4A, 0x2E)),
            _ => new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C))
        };
}
