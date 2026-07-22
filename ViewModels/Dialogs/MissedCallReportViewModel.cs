using Replixer.Infrastructure;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public record MissedCallReportData(
    string Manager,
    string CallType,
    string CrmUrl,
    IReadOnlyList<string> ScreenshotUrls,
    // Час "первого касання" для Kommo — за замовчуванням момент кліку на "Не додзвонився",
    // але для типу "ще не було спілкування" менеджер міг скоригувати його вручну у формі.
    DateTime FirstContactTime)
{
    public string FormatCaption()
    {
        var sb = new StringBuilder();
        sb.AppendLine("📋 Звіт по дзвінку");
        sb.AppendLine();
        sb.AppendLine($"👤 Менеджер: {Manager}");
        sb.AppendLine($"📞 Тип дзвінка: {CallType}");
        if (!string.IsNullOrWhiteSpace(CrmUrl))
            sb.AppendLine($"🔗 CRM: {CrmUrl}");

        for (int i = 0; i < ScreenshotUrls.Count; i++)
            sb.AppendLine($"📎 Скрін {i + 1} - {ScreenshotUrls[i]}");

        return sb.ToString().TrimEnd();
    }
}

public class MissedCallReportViewModel : ViewModelBase
{
    public static IReadOnlyList<string> CallTypes { get; } = new[]
    {
        "Недодзвон (ще не було спілкування)", "Недодзвон (вже було спілкування)"
    };

    private const string NoCommunicationCallType = "Недодзвон (ще не було спілкування)";
    private const string FirstContactTimeFormat  = "dd.MM.yyyy HH:mm";

    private readonly string _managerName;
    // Момент кліку на "Не додзвонився" — стартове значення поля часу і запасний варіант,
    // якщо блок редагування прихований (тип не "ще не було спілкування") чи текст невалідний.
    private readonly DateTime _defaultFirstContactTime;
    private string? _selectedCallType;
    private string _crmUrl = string.Empty;
    private string _firstContactTimeText;

    private readonly Action<MissedCallReportData?> _onComplete;

    public string? SelectedCallType
    {
        get => _selectedCallType;
        set
        {
            if (SetField(ref _selectedCallType, value))
            {
                EnsureRequiredScreenshotFields();
                OnPropertyChanged(nameof(HasCallType));
                OnPropertyChanged(nameof(IsFirstContactTimeEditable));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // Керує видимістю блоку скрінів і кнопки "+ Додати ще поле" — обидва з'являються
    // лише після вибору типу дзвінка.
    public bool HasCallType => _selectedCallType is not null;

    // Ручне редагування часу "первого касання" доступне лише для "ще не було спілкування" —
    // саме цей тип надалі переводить лід у статус "Недозвон" у Kommo, тож там важливо мати
    // точний час першої спроби, а не лише момент, коли менеджер натиснув кнопку в застосунку.
    public bool IsFirstContactTimeEditable => _selectedCallType == NoCommunicationCallType;

    public string FirstContactTimeText
    {
        get => _firstContactTimeText;
        set
        {
            if (SetField(ref _firstContactTimeText, value))
            {
                OnPropertyChanged(nameof(IsFirstContactTimeInvalid));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // true лише коли блок видимий, але текст не парситься як дд.мм.рррр гг:хх — для підсвітки помилки.
    public bool IsFirstContactTimeInvalid => IsFirstContactTimeEditable && !TryParseFirstContactTime(out _);

    private bool TryParseFirstContactTime(out DateTime value) =>
        DateTime.TryParseExact(
            _firstContactTimeText?.Trim(), FirstContactTimeFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    // Кнопки +/- біля поля: якщо поточний текст валідний — крутимо від нього, інакше
    // (менеджер щось зіпсував руками) відштовхуємось від часу кліку на "Не додзвонився".
    private void AdjustFirstContactTime(int minutesDelta)
    {
        var baseTime = TryParseFirstContactTime(out var parsed) ? parsed : _defaultFirstContactTime;
        FirstContactTimeText = baseTime.AddMinutes(minutesDelta).ToString(FirstContactTimeFormat, CultureInfo.InvariantCulture);
    }

    // "Ще не було спілкування" — довший тип недодзвону, менеджер має докласти 2 скріни
    // (напр. дзвінок і повідомлення); "вже було спілкування" — досить одного.
    private int RequiredScreenshotCount => _selectedCallType switch
    {
        "Недодзвон (ще не було спілкування)" => 2,
        "Недодзвон (вже було спілкування)"   => 1,
        _                                     => 0,
    };

    public string CrmUrl
    {
        get => _crmUrl;
        set
        {
            if (SetField(ref _crmUrl, value))
            {
                OnPropertyChanged(nameof(IsCrmUrlInvalid));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // true лише коли поле не порожнє, але текст не є коректним посиланням — для підсвітки помилки в UI.
    public bool IsCrmUrlInvalid => !string.IsNullOrWhiteSpace(_crmUrl) && !UrlValidator.IsValidHttpUrl(_crmUrl);

    public ObservableCollection<ScreenshotAttachment> Screenshots { get; } = new();

    public ICommand AddScreenshotFieldCommand { get; }
    public ICommand RemoveScreenshotFieldCommand { get; }
    public ICommand IncrementFirstContactTimeCommand { get; }
    public ICommand DecrementFirstContactTimeCommand { get; }
    public ICommand SubmitCommand { get; }

    public MissedCallReportViewModel(Action<MissedCallReportData?> onComplete, DateTime missedAt, string? managerName = null)
    {
        _onComplete              = onComplete;
        _managerName             = managerName ?? string.Empty;
        _defaultFirstContactTime = missedAt;
        _firstContactTimeText    = missedAt.ToString(FirstContactTimeFormat, CultureInfo.InvariantCulture);
        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);

        AddScreenshotFieldCommand = new RelayCommand(
            () => Screenshots.Add(new ScreenshotAttachment(Screenshots.Count + 1, isRemovable: true)));
        RemoveScreenshotFieldCommand = new RelayCommand<ScreenshotAttachment>(RemoveScreenshotField);

        // Кнопки +/- поруч із полем часу — крок 1 хв за клік (RepeatButton у XAML сам
        // повторює команду, доки кнопку тримають натиснутою).
        IncrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(1));
        DecrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(-1));

        Screenshots.CollectionChanged += OnScreenshotsCollectionChanged;
        // Поля скрінів з'являються лише після вибору типу дзвінка (див. EnsureRequiredScreenshotFields) —
        // доки тип не обрано, полів не повинно бути взагалі.
    }

    private void RemoveScreenshotField(ScreenshotAttachment? item)
    {
        if (item is null || !item.IsRemovable) return;

        Screenshots.Remove(item);

        // Перенумеровуємо мітки "Скріншот N", щоб після видалення поля з середини
        // вони лишались послідовними (1, 2, 3...).
        for (int i = 0; i < Screenshots.Count; i++)
            Screenshots[i].Index = i + 1;
    }

    // Синхронізує кількість полів скрінів РІВНО з мінімумом, потрібним для щойно обраного
    // типу дзвінка: тип не обрано → полів немає; "вже було спілкування" → 1; "ще не було" → 2.
    // Спрацьовує лише при зміні самого типу дзвінка, тож поля, додані вручну через
    // "+ Додати ще поле" вже ПІСЛЯ вибору типу, цим не зачіпаються.
    private void EnsureRequiredScreenshotFields()
    {
        while (Screenshots.Count < RequiredScreenshotCount)
            Screenshots.Add(new ScreenshotAttachment(Screenshots.Count + 1));

        while (Screenshots.Count > RequiredScreenshotCount)
            Screenshots.RemoveAt(Screenshots.Count - 1);
    }

    // Перевалідовуємо кнопку "Відправити" при кожній зміні тексту в полі скріншота
    // (щоб некоректне посилання на prnt.sc одразу блокувало відправку) і стежимо
    // за появою/зникненням нових полів, додаваних через AddScreenshotFieldCommand.
    private void OnScreenshotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ScreenshotAttachment item in e.NewItems)
                item.PropertyChanged += OnScreenshotItemPropertyChanged;

        if (e.OldItems is not null)
            foreach (ScreenshotAttachment item in e.OldItems)
                item.PropertyChanged -= OnScreenshotItemPropertyChanged;

        CommandManager.InvalidateRequerySuggested();
    }

    private void OnScreenshotItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenshotAttachment.IsInvalid))
            CommandManager.InvalidateRequerySuggested();
    }

    // Усі поля скрінів — і обов'язкові, і додані вручну через "+ Додати ще поле" — мають бути
    // заповнені коректним посиланням: якщо менеджер додав зайве поле, "Відправити" лишається
    // неактивною, доки він або заповнить його, або видалить.
    private bool CanSubmit() =>
        _selectedCallType is not null &&
        UrlValidator.IsValidHttpUrl(_crmUrl) &&
        Screenshots.Count >= RequiredScreenshotCount &&
        Screenshots.All(s => UrlValidator.IsValidScreenshotUrl(s.Url)) &&
        !IsFirstContactTimeInvalid;

    private void OnSubmit()
    {
        // Якщо блок редагування прихований (тип не "ще не було спілкування") або текст
        // з якоїсь причини не спарсився — використовуємо момент кліку на "Не додзвонився".
        var firstContactTime = IsFirstContactTimeEditable && TryParseFirstContactTime(out var edited)
            ? edited
            : _defaultFirstContactTime;

        _onComplete(new MissedCallReportData(
            Manager:          _managerName,
            CallType:         _selectedCallType!,
            CrmUrl:           _crmUrl,
            ScreenshotUrls:   Screenshots
                .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => s.Url.Trim())
                .ToList(),
            FirstContactTime: firstContactTime));
    }
}
