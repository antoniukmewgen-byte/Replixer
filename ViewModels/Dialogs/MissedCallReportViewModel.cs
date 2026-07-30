using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Upload;
using Replixer.Views;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public record MissedCallReportData(
    string Manager,
    string CallType,
    string CrmUrl,
    IReadOnlyList<string> ScreenshotUrls,
    // Час "первого касання" для Kommo — за замовчуванням момент кліку на "Не додзвонився",
    // але для типу "ще не було спілкування" менеджер міг скоригувати його вручну у формі.
    DateTime FirstContactTime,
    // Ті самі посилання, що й ScreenshotUrls, але прив'язані до конкретного месенджера
    // ("Viber"/"Telegram"/"WhatsApp" — див. MessengerDeepLinkProvider.SupportedMessengers).
    // ScreenshotUrls — компактний список (без урахування, якого саме месенджера скрін) для
    // Kommo-нотатки, де порядок не важливий; цей словник потрібен там, де важлива саме
    // прив'язка до месенджера — колонки Google Таблиці (BuildSheetRow) мають назви "Viber"/
    // "Telegram"/"WhatsApp", тож без нього скрін не того месенджера міг би потрапити не в ту колонку.
    IReadOnlyDictionary<string, string>? ScreenshotUrlsByMessenger = null)
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
    private readonly GoogleDriveUploadService _drive;
    private readonly AppSettings _settings;
    private readonly PendingScreenshotUploadRetryService _screenshotRetry;
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
                OnPropertyChanged(nameof(IsFirstContactTimeEditable));
                // RequiredScreenshotCount залежить від типу дзвінка (2 для "ще не було
                // спілкування", 1 для "вже було спілкування") — тож підказка й доступність
                // "Відправити" мають переоцінюватись і при зміні типу, а не лише при новому скріні.
                OnPropertyChanged(nameof(HasEnoughScreenshots));
                OnPropertyChanged(nameof(ShowScreenshotHint));
                OnPropertyChanged(nameof(ScreenshotHintText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

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

    public ICommand IncrementFirstContactTimeCommand { get; }
    public ICommand DecrementFirstContactTimeCommand { get; }
    public ICommand SubmitCommand { get; }

    // "Швидкі дії" — відкрити чат з клієнтом у месенджері за номером з CRM і автоматично
    // зняти скрін, щойно вікно месенджера стане активним (без окремого плаваючого віджета).
    public IReadOnlyList<string> Messengers => MessengerDeepLinkProvider.SupportedMessengers;
    public ICommand OpenMessengerCommand { get; }

    // Окремий статус/результат на кожен месенджер — відображається у відповідному
    // (нередагованому вручну) полі поруч із його іконкою, а не в одному спільному рядку.
    private string? _viberStatus;
    public string? ViberStatus
    {
        get => _viberStatus;
        private set => SetField(ref _viberStatus, value);
    }

    private string? _telegramStatus;
    public string? TelegramStatus
    {
        get => _telegramStatus;
        private set => SetField(ref _telegramStatus, value);
    }

    private string? _whatsAppStatus;
    public string? WhatsAppStatus
    {
        get => _whatsAppStatus;
        private set => SetField(ref _whatsAppStatus, value);
    }

    // Справжня валідація стану поля, а не лише текст: поле статусу месенджера показує то
    // проміжні повідомлення ("Шукаю номер клієнта…"), то помилку, то (в успішному фіналі)
    // саме посилання на Drive — і UI має відрізняти "помилка" від решти (щоб підсвітити поле
    // так само, як CRM/час першого касання підсвічуються через IsCrmUrlInvalid/
    // IsFirstContactTimeInvalid), а не покладатись на вміст рядка.
    private bool _viberActionFailed;
    public bool ViberActionFailed
    {
        get => _viberActionFailed;
        private set => SetField(ref _viberActionFailed, value);
    }

    private bool _telegramActionFailed;
    public bool TelegramActionFailed
    {
        get => _telegramActionFailed;
        private set => SetField(ref _telegramActionFailed, value);
    }

    private bool _whatsAppActionFailed;
    public bool WhatsAppActionFailed
    {
        get => _whatsAppActionFailed;
        private set => SetField(ref _whatsAppActionFailed, value);
    }

    // isError=true — це не просто чергове проміжне повідомлення, а кінцева невдача цього
    // кроку (немає номера, не відкрився месенджер, не зняли скрін, не залили на Drive тощо).
    // isError=false скидає попередню помилку — використовується і на старті нової спроби,
    // і в момент фінального успіху (посилання).
    private void SetMessengerStatus(string messenger, string? value, bool isError = false)
    {
        switch (messenger)
        {
            case "Viber":    ViberStatus    = value; ViberActionFailed    = isError; break;
            case "Telegram": TelegramStatus = value; TelegramActionFailed = isError; break;
            case "WhatsApp": WhatsAppStatus = value; WhatsAppActionFailed = isError; break;
        }
    }

    // Посилання на Google Drive окремо від тексту статусу — у ViberStatus/TelegramStatus/
    // WhatsAppStatus разом зі станом можуть тимчасово опинитись службові повідомлення
    // ("Завантажую на Google Drive…", помилки тощо), тому в ScreenshotUrls мають потрапити
    // лише реально завантажені посилання, а не будь-який поточний текст поля.
    private string? _viberScreenshotUrl;
    private string? _telegramScreenshotUrl;
    private string? _whatsAppScreenshotUrl;

    // "Ще не було спілкування" — довший тип недодзвону, менеджер має докласти 2 скріни
    // (напр. дзвінок і повідомлення); "вже було спілкування" — досить одного.
    private int RequiredScreenshotCount => IsNoCommunicationType(_selectedCallType) ? 2 : 1;

    // Кнопка "Відправити" вимагає щонайменше RequiredScreenshotCount реально завантажених
    // скріншотів як доказу переписки (див. CanSubmit) — тому UI показує підказку, поки їх бракує.
    public bool HasEnoughScreenshots => CollectScreenshotUrls().Count >= RequiredScreenshotCount;

    // Підказку показуємо лише коли тип дзвінка вже обрано — доки _selectedCallType == null,
    // RequiredScreenshotCount ще не має сенсу (невідомо, 1 чи 2 скріни знадобляться), тож
    // показувати текст завчасно було б оманливо.
    public bool ShowScreenshotHint => _selectedCallType is not null && !HasEnoughScreenshots;

    // Текст підказки під кнопкою "Відправити" — залежить від того, скільки скрінів ще бракує
    // для поточного типу дзвінка (2 для "ще не було спілкування", 1 — для "вже було").
    public string ScreenshotHintText => RequiredScreenshotCount == 2
        ? "Зробіть два скріншоти переписки, щоб відправити звіт"
        : "Зробіть один скріншот переписки, щоб відправити звіт";

    private void SetMessengerScreenshotUrl(string messenger, string? url)
    {
        switch (messenger)
        {
            case "Viber":    _viberScreenshotUrl    = url; break;
            case "Telegram": _telegramScreenshotUrl = url; break;
            case "WhatsApp": _whatsAppScreenshotUrl = url; break;
        }
        OnPropertyChanged(nameof(HasEnoughScreenshots));
        OnPropertyChanged(nameof(ShowScreenshotHint));
    }

    private IReadOnlyList<string> CollectScreenshotUrls()
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(_viberScreenshotUrl))    urls.Add(_viberScreenshotUrl);
        if (!string.IsNullOrWhiteSpace(_telegramScreenshotUrl)) urls.Add(_telegramScreenshotUrl);
        if (!string.IsNullOrWhiteSpace(_whatsAppScreenshotUrl)) urls.Add(_whatsAppScreenshotUrl);
        return urls;
    }

    // На відміну від CollectScreenshotUrls (компактний список без прив'язки до месенджера),
    // тут кожне посилання йде під ключем свого месенджера — щоб у Google Таблиці скрін
    // гарантовано потрапив саме в колонку "Viber"/"Telegram"/"WhatsApp", а не в довільну за порядком.
    private IReadOnlyDictionary<string, string> CollectScreenshotUrlsByMessenger()
    {
        var map = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(_viberScreenshotUrl))    map["Viber"]    = _viberScreenshotUrl;
        if (!string.IsNullOrWhiteSpace(_telegramScreenshotUrl)) map["Telegram"] = _telegramScreenshotUrl;
        if (!string.IsNullOrWhiteSpace(_whatsAppScreenshotUrl)) map["WhatsApp"] = _whatsAppScreenshotUrl;
        return map;
    }

    public MissedCallReportViewModel(
        Action<MissedCallReportData?> onComplete,
        DateTime missedAt,
        KommoService kommo,
        ScreenCaptureService screenCapture,
        GoogleDriveUploadService driveUpload,
        AppSettings settings,
        PendingScreenshotUploadRetryService screenshotRetry,
        string? managerName = null)
    {
        _onComplete              = onComplete;
        _managerName             = managerName ?? string.Empty;
        _defaultFirstContactTime = missedAt;
        _kommo                   = kommo;
        _capture                 = screenCapture;
        _drive                   = driveUpload;
        _settings                = settings;
        _screenshotRetry         = screenshotRetry;
        _firstContactTimeText    = missedAt.ToString(FirstContactTimeFormat, CultureInfo.InvariantCulture);
        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);

        // Кнопки +/- поруч із полем часу — крок 1 хв за клік (RepeatButton у XAML сам
        // повторює команду, доки кнопку тримають натиснутою).
        IncrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(1));
        DecrementFirstContactTimeCommand = new RelayCommand(() => AdjustFirstContactTime(-1));

        OpenMessengerCommand = new AsyncRelayCommand<string>(OpenMessengerAsync);
    }

    private async Task OpenMessengerAsync(string? messenger)
    {
        if (string.IsNullOrWhiteSpace(messenger)) return;

        if (string.IsNullOrWhiteSpace(_crmUrl) || !UrlValidator.IsValidHttpUrl(_crmUrl))
        {
            SetMessengerStatus(messenger, "Спочатку вкажіть коректне посилання на CRM", isError: true);
            return;
        }

        SetMessengerStatus(messenger, "Шукаю номер клієнта в CRM…");
        string? phone;
        try
        {
            phone = await _kommo.GetClientPhoneAsync(_crmUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] GetClientPhoneAsync failed: {ex}");
            SetMessengerStatus(messenger, "Помилка звернення до CRM", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            SetMessengerStatus(messenger, "Не вдалося знайти номер телефону клієнта в CRM", isError: true);
            return;
        }

        var link = MessengerDeepLinkProvider.BuildDeepLink(messenger, phone);
        if (link is null)
        {
            SetMessengerStatus(messenger, $"Немає посилання для {messenger}", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] Process.Start failed: {ex}");
            SetMessengerStatus(messenger, $"Не вдалося відкрити {messenger}", isError: true);
            return;
        }

        SetMessengerStatus(messenger, $"Відкриваю {messenger}, очікую вікно…");

        var appeared = await WaitForMessengerForegroundAsync(messenger);
        if (!appeared)
        {
            SetMessengerStatus(messenger, $"Вікно {messenger} не з'явилось — скрін не зроблено", isError: true);
            return;
        }

        // Фіксуємо вікно месенджера "поверх усіх" на весь час підбору рамки — інакше будь-яке
        // інше вікно (тост, спливаюче нагадування тощо) могло б випадково перекрити його якраз
        // під час паузи нижче чи ручного ресайзу, і "дірка" в оверлеї показувала б не той вміст.
        // Знімаємо фіксацію в finally незалежно від результату, щоб вікно не лишилось приліпленим.
        var hWnd = ScreenCaptureInterop.GetForegroundWindow();
        ScreenCaptureInterop.SetWindowTopmost(hWnd, pin: true);
        try
        {
            await Task.Delay(RenderSettleDelay);

            if (!ScreenCaptureInterop.GetWindowRect(hWnd, out var winRect))
            {
                SetMessengerStatus(messenger, "Не вдалося визначити межі вікна — скрін не зроблено", isError: true);
                return;
            }

            var initialRect = new Int32Rect(winRect.Left, winRect.Top, winRect.Right - winRect.Left, winRect.Bottom - winRect.Top);

            SetMessengerStatus(messenger, $"Підберіть область скріна для {messenger}…");

            // Модально блокує потік до підтвердження/скасування — це навмисно: поки менеджер
            // підбирає рамку, взаємодія з рештою застосунку/системи заблокована самим оверлеєм
            // (ScreenshotSelectionWindow — не click-through), тож немає сенсу асинхронно
            // повертати керування UI раніше.
            var chosen = ScreenshotSelectionWindow.Show(initialRect);
            if (chosen is null)
            {
                SetMessengerStatus(messenger, "Скріншот скасовано");
                return;
            }

            await CaptureAndUploadScreenshotAsync(messenger, chosen.Value);
        }
        finally
        {
            ScreenCaptureInterop.SetWindowTopmost(hWnd, pin: false);
        }
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

    // Знімає скрін вікна месенджера локально, вантажить його на Google Drive (в окрему підпапку
    // "Screenshots", а не впереміш із аудіозаписами дзвінків) і записує отримане посилання прямо
    // у поле статусу відповідного месенджера (те саме readonly-поле, що раніше показувало лише
    // "Скріншот збережено локально"). Локальний файл видаляється одразу після успішного
    // завантаження — Drive стає єдиним місцем зберігання скріна. Якщо завантажити одразу не
    // вдалось (мережа тощо) — файл лишається на диску і ставиться в чергу фонового ретраю
    // (_screenshotRetry), щоб посилання не загубилось назавжди.
    private async Task CaptureAndUploadScreenshotAsync(string messenger, Int32Rect region)
    {
        var path = _capture.CaptureRegion(region.X, region.Y, region.Width, region.Height, messenger);
        if (path is null)
        {
            SetMessengerStatus(messenger, "Не вдалося зробити скріншот", isError: true);
            return;
        }

        SetMessengerStatus(messenger, "Завантажую скріншот на Google Drive…");

        string? folderId;
        try
        {
            folderId = await ResolveScreenshotFolderIdAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] Screenshot folder resolve failed: {ex}");
            folderId = null;
        }

        string? url;
        try
        {
            url = await _drive.UploadAsync(path, folderId, mimeType: "image/png");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MissedCallReport] Screenshot upload failed: {ex}");
            url = null;
        }

        if (url is null)
        {
            SetMessengerStatus(messenger, "Не вдалося завантажити — спробуємо у фоні пізніше", isError: true);
            // CrmUrl тут уже гарантовано валідний (перевірено на вході в OpenMessengerAsync) —
            // передаємо його, щоб, якщо форму встигнуть відправити раніше за фонову спробу,
            // посилання все одно потрапило в лід окремою нотаткою (див. RetryOneAsync).
            _ = _screenshotRetry.EnqueueAsync(path, folderId, messenger, _crmUrl);
            return;
        }

        SetMessengerScreenshotUrl(messenger, url);
        SetMessengerStatus(messenger, url); // isError: false (за замовчуванням) — це вже справжнє посилання

        try { File.Delete(path); }
        catch (Exception ex) { Debug.WriteLine($"[MissedCallReport] Local screenshot delete failed: {ex.Message}"); }
    }

    // Кешується на весь час життя діалогу — щоб не звертатись до Drive API за кожен скрін
    // (Viber/Telegram/WhatsApp), а резолвити підпапку "Screenshots" лише один раз.
    private string? _screenshotFolderId;
    private bool _screenshotFolderResolved;

    // Базова папка — той самий спрощений (порівняно з UploadOrchestrator.ResolveTargetFolderAsync)
    // вибір: без авто-створення підпапки менеджера, лише вже готові UserFolderId чи спільна
    // GoogleDriveFolderId. Усередині неї додатково гарантується/створюється підпапка "Screenshots",
    // щоб скріни не лежали впереміш із аудіозаписами дзвінків у тій самій папці.
    private async Task<string?> ResolveScreenshotFolderIdAsync()
    {
        if (_screenshotFolderResolved) return _screenshotFolderId;

        var baseFolderId = ResolveBaseFolderId();
        if (string.IsNullOrWhiteSpace(baseFolderId))
        {
            _screenshotFolderId = null;
        }
        else
        {
            var subfolderId = await _drive.GetOrCreateUserFolderAsync(baseFolderId, "Screenshots");
            _screenshotFolderId = subfolderId ?? baseFolderId; // якщо підпапку створити не вдалось — вантажимо в базову
        }

        _screenshotFolderResolved = true;
        return _screenshotFolderId;
    }

    private string? ResolveBaseFolderId()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserFolderId))
            return _settings.UserFolderId;

        return string.IsNullOrWhiteSpace(_settings.GoogleDriveFolderId)
            ? null
            : _settings.GoogleDriveFolderId;
    }

    // Ручне введення посилань на скріншоти (поля "Скріншот N" + "Додати ще скріншот") прибрано —
    // наразі скрін знімається автоматично через "Швидкі дії" і одразу вантажиться на Google Drive
    // (CaptureAndUploadScreenshotAsync), а MissedCallReportData.ScreenshotUrls формується
    // з реально завантажених посилань у CollectScreenshotUrls().
    private bool CanSubmit() =>
        _selectedCallType is not null &&
        UrlValidator.IsValidHttpUrl(_crmUrl) &&
        !IsFirstContactTimeInvalid &&
        CollectScreenshotUrls().Count >= RequiredScreenshotCount;

    private void OnSubmit()
    {
        // Якщо блок редагування прихований (тип не "ще не було спілкування") або текст
        // з якоїсь причини не спарсився — використовуємо момент кліку на "Не додзвонився".
        var firstContactTime = IsFirstContactTimeEditable && TryParseFirstContactTime(out var edited)
            ? edited
            : _defaultFirstContactTime;

        _onComplete(new MissedCallReportData(
            Manager:                   _managerName,
            CallType:                  _selectedCallType!,
            CrmUrl:                    _crmUrl,
            ScreenshotUrls:            CollectScreenshotUrls(),
            FirstContactTime:          firstContactTime,
            ScreenshotUrlsByMessenger: CollectScreenshotUrlsByMessenger()));
    }
}
