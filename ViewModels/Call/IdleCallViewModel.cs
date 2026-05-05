using EchoVault.Infrastructure;
using System.Windows.Input;

namespace EchoVault.ViewModels.Call;

public class IdleCallViewModel : ViewModelBase
{
    public ICommand RecordManuallyCommand { get; }

    public IdleCallViewModel(Action onStartRecording)
    {
        RecordManuallyCommand = new RelayCommand(onStartRecording);
    }
}
