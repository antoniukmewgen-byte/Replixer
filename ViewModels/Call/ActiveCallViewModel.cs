using Replixer.Infrastructure;
using System.Windows.Input;
using System.Windows.Threading;

namespace Replixer.ViewModels.Call;

public class ActiveCallViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private TimeSpan _elapsed = TimeSpan.Zero;

    public string ElapsedTime => _elapsed.ToString(@"hh\:mm\:ss");

    public ICommand StopCommand { get; }

    public ActiveCallViewModel(Action onStop)
    {
        StopCommand = new RelayCommand(onStop);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
            OnPropertyChanged(nameof(ElapsedTime));
        };
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
