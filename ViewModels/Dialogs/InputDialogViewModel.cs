using Replixer.Infrastructure;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public sealed class InputDialogViewModel : ViewModelBase
{
    private string _inputText = string.Empty;

    public string Prompt { get; }

    public string InputText
    {
        get => _inputText;
        set => SetField(ref _inputText, value);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand  { get; }

    public InputDialogViewModel(string prompt, Action<string?> onComplete)
    {
        Prompt = prompt;
        ConfirmCommand = new RelayCommand(() => onComplete(InputText.Trim()));
        CancelCommand  = new RelayCommand(() => onComplete(null));
    }
}
