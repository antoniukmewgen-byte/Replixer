using Replixer.Infrastructure;
using System.Windows.Input;

namespace Replixer.ViewModels.Call;

public class IdleCallViewModel : ViewModelBase
{
    public ICommand RecordManuallyCommand { get; }

    public IdleCallViewModel(Action onStartRecording)
    {
        RecordManuallyCommand = new RelayCommand(onStartRecording);
    }
}
