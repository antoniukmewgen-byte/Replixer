using Replixer.ViewModels.Dialogs;

namespace Replixer.ViewModels;

public interface IDialogHost
{
    void ShowInputDialog(InputDialogViewModel vm);
    void HideInputDialog();
}
