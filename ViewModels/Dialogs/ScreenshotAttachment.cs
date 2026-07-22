using Replixer.Infrastructure;

namespace Replixer.ViewModels.Dialogs;

public class ScreenshotAttachment : ViewModelBase
{
    private string _url = string.Empty;
    private int _index;

    // Індекс переприсвоюється при видаленні сусіднього поля, щоб мітки "Скріншот N"
    // лишались послідовними — тому settable, а не readonly.
    public int Index
    {
        get => _index;
        set
        {
            if (SetField(ref _index, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => $"Скріншот {Index}";

    // true для полів, доданих вручну через "+ Додати ще поле" — на відміну від обов'язкових
    // полів, які підлаштовуються під обраний тип дзвінка, такі можна видалити назад.
    public bool IsRemovable { get; }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value ?? string.Empty))
                OnPropertyChanged(nameof(IsInvalid));
        }
    }

    // true лише коли поле не порожнє, але текст не є коректним посиланням на prnt.sc — для підсвітки помилки в UI.
    public bool IsInvalid => !string.IsNullOrWhiteSpace(_url) && !UrlValidator.IsValidScreenshotUrl(_url);

    public ScreenshotAttachment(int index, bool isRemovable = false)
    {
        _index      = index;
        IsRemovable = isRemovable;
    }
}
