using EchoVault.Infrastructure;
using System.Windows.Input;

namespace EchoVault.ViewModels.Call;

public class ActiveCallViewModel : ViewModelBase
{
    public ICommand StopCommand { get; }

    public ActiveCallViewModel(Action onStop)
    {
        StopCommand = new RelayCommand(onStop);
    }
}
