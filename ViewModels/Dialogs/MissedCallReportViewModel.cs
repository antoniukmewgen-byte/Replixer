using Replixer.Infrastructure;
using Replixer.Services;
using Replixer.Services.Upload;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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
        "Недозвон 1 (ще не було спілкування)",
        "Недозвон 2 (ще не було спілкування)",
        "Недозвон 3 (ще не було спілкування)",
        "Недозвон 4 (ще не було спілкування)",
        "Недодзвон (вже було спілкування)",
    };

    private const string NoCommunicationMarker  = "ще не було спілкування";
    private const string FirstContactTimeFormat = "dd.MM.yyyy HH:mm";

    // "Недозвон 1..4 (ще не було спілкування)" усі поводяться однаково — перевіряємо підрядком,
    // а не точним значенням зі списку CallTypes.
    private static bool IsNoCommunicationType(string? callType) =>
        callType is not null && callType.Contains(NoCommunicationMarker);

    // Скільки чекати, поки вікно месенджера стане foreground, перш ніж здатися.
    private static readonly TimeSpan ForegroundWaitTimeout  = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(150);
    // Невелика пауза після появи вікна месенджера, щоб воно встигло промалюватись
    // (інакше PrintWindow може захопити ще порожній/недомальований кадр).
    private static readonly TimeSpan RenderSettleDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _managerName;
    // Момент кліку на "Не додзвонився" — стартове значення поля часу і запасний варіант,
    // якщо блок редагування прихований (тип не "ще не було спілкування") чи текст невалідний.
    private readonly DateTime _defaultFirstContactTime;
    private readonly KommoService _kommo;
    private readonly ScreenCaptureService _capture;
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
    public bool IsFirstContactTimeEditable => IsNoCommunicationType(_selectedCallType);

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
        null                                          => 0,
        _ when IsNoCommunicationType(_selectedCallType) => 2,
        "Недодзвон (вже було спілкування)"              => 1,
        _                                                => 0,
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

    // "Швидкі дії" — відкрити чат з клієнтом у месенджері за номером з CRM і автоматично
    // зняти скрін, щойно вікно месенджера стане активним (без окремого плаваючого віджета).
    public IReadOnlyList<string> Messengers => MessengerDeepLinkProvider.SupportedMessengers;
    public ICommand OpenMessengerCommand { get; }

    private string? _quickActionsStatus;
    public string? QuickActionsStatus
    {
        get => _quickActionsStatus;
        private set => SetField(ref _quickActionsStatus, value);
    }

    public MissedCallReportViewModel(
        Action<MissedCallReportData?> onComplete,
        DateTime missedAt,
        KommoService kommo,
        ScreenCaptureService screenCapture,
        string? managerName = null)
    {
        _onComplete              = onComplete;
        _managerName             = managerName ?? string.Empty;
        _defaultFirstContactTime = missedAt;
        _kommo                   = kommo;
        _capture                 = screenCapture;
        _firstContactTimeText    = missedAt.ToString(FirstContactTimeFormat, CultureInfo.InvariantCulture);
        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);

        AddScreenshotFieldCommand = new RelayCommand(
            () => Screenshots.Add(new ScreenshotAttachment(Screenshots.Count + 1, isRemovable: true)));
        RemoveScreenshotFieldCommand = new RelayCommand<ScreenshotAttachment>(RemoveScreenshotField);

        // Кнопки +/- поруч із полем часу — крок 1 хв за клік (RepeatButton у XAML сам
        // повторює команду, доки кнопку тримають натиснутою).
        IncrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(1));
        DecrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(-1));

        OpenMessengerCommand = new AsyncRelayCommand<string>(OpenMessengerAsync);

        Screenshots.CollectionChanged += OnScreenshotsCollectionChanged;
        // Поля скрінів з'являються лише після вибору типу дзвінка (див. EnsureRequiredScreenshotFields) —
        // доки тип не обрано, полів не повинно бути взагалі.
    }

    private async Task OpenMessengerAsync(string? messenger)
    {
        if (string.IsNullOrWhiteSpace(messenger)) return;

        if (string.IsNullOrWhiteSpace(_crmUrl) || !UrlValidator.IsValidHttpUrl(_crmUrl))
        {
            QuickActionsStatus = "Спочатку вкажіть коректне посилання на CRM";
            return;
        }

        QuickActionsStatus = "Шукаю номер клієнта в CRM…";
        string? phone;
        try
        {
            phone = await _kommo.GetClientPhoneAsync(_crmUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] GetClientPhoneAsync failed: {ex}");
            QuickActionsStatus = "Помилка звернення до CRM";
            return;
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            QuickActionsStatus = "Не вдалося знайти номер телефону клієнта в CRM";
            return;
        }

        var link = MessengerDeepLinkProvider.BuildDeepLink(messenger, phone);
        if (link is null)
        {
            QuickActionsStatus = $"Немає посилання для {messenger}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] Process.Start failed: {ex}");
            QuickActionsStatus = $"Не вдалося відкрити {messenger}";
            return;
        }

        QuickActionsStatus = $"Відкриваю {messenger}, очікую вікно…";

        var appeared = await WaitForMessengerForegroundAsync(messenger);
        if (!appeared)
        {
            QuickActionsStatus = $"Вікно {messenger} не з'явилось — скрін не зроблено";
            return;
        }

        await Task.Delay(RenderSettleDelay);
        CaptureScreenshot();
    }

    // Опитує GetForegroundWindow(), поки активним вікном не стане процес потрібного
    // месенджера, або поки не вийде час очікування. Для Store/UWP-застосунків (типово —
    // WhatsApp) процес foreground-вікна може виявитись не самим месенджером, а хостом
    // (ApplicationFrameHost) — у цьому випадку MessengerProcessNames.Matches додатково
    // звіряє заголовок вікна. ПРИМІТКА: якщо посилання відкриється спочатку в браузері,
    // а не напряму в десктопному застосунку, це очікування може не спрацювати — потребує
    // перевірки на реальній машині замовника.
    private static async Task<bool> WaitForMessengerForegroundAsync(string messenger)
    {
        var deadline = DateTime.UtcNow + ForegroundWaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var hWnd = ScreenCaptureInterop.GetForegroundWindow();
            if (hWnd != IntPtr.Zero && ScreenCaptureInterop.GetWindowThreadProcessId(hWnd, out var pid) != 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (MessengerProcessNames.Matches(messenger, proc.ProcessName, GetWindowTitle(hWnd)))
                        return true;
                }
                catch (ArgumentException)
                {
                    // Процес встиг завершитись між викликами — просто пробуємо далі.
                }
            }

            await Task.Delay(ForegroundPollInterval);
        }

        return false;
    }

    private static string? GetWindowTitle(IntPtr hWnd)
    {
        var length = ScreenCaptureInterop.GetWindowTextLength(hWnd);
        if (length <= 0) return null;

        var sb = new StringBuilder(length + 1);
        return ScreenCaptureInterop.GetWindowText(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    private void CaptureScreenshot()
    {
        var path = _capture.CaptureForegroundWindow();
        QuickActionsStatus = path is null
            ? "Не вдалося зробити скріншот"
            : "Скріншот збережено локально";
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
