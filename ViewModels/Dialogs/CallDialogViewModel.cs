using EchoVault.Infrastructure;
using System.Windows.Input;

namespace EchoVault.ViewModels.Dialogs;

public class CallDialogViewModel : ViewModelBase
{
    public string Title { get; }
    public string Message { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }

    public CallDialogViewModel(
        string title,
        string message,
        string primaryLabel, Action onPrimary,
        string secondaryLabel, Action onSecondary)
    {
        Title = title;
        Message = message;
        PrimaryLabel = primaryLabel;
        SecondaryLabel = secondaryLabel;
        PrimaryCommand = new RelayCommand(onPrimary);
        SecondaryCommand = new RelayCommand(onSecondary);
    }
}
