using Replixer.Infrastructure;
using System.Text;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public record CallReportData(
    string Manager,
    string CallType,
    string? CustomCallType,
    string LeadSource,
    string CrmUrl,
    string Rating,
    string Outcome,
    bool? IsInvoicePaid,
    string PaymentProbability,
    string Note)
{
    // Not a positional parameter — keeps old serialised records compatible (defaults to "Менеджер").
    public string Position { get; init; } = "Менеджер";

    public string FormatCaption()
    {
        var callType = CallType == "Інший" && !string.IsNullOrWhiteSpace(CustomCallType)
            ? CustomCallType! : CallType;

        var outcome = Outcome;
        if (Outcome == "Виставив рахунок" && IsInvoicePaid.HasValue)
            outcome = $"Виставив рахунок ({(IsInvoicePaid.Value ? "Оплатив ✅" : "Не оплатив ❌")})";

        var sb = new StringBuilder();
        sb.AppendLine("📋 Звіт по дзвінку");
        sb.AppendLine();
        sb.AppendLine($"👤 {Position}: {Manager}");
        sb.AppendLine($"📞 Тип дзвінка: {callType}");
        sb.AppendLine($"📣 Джерело ліда: {LeadSource}");
        if (!string.IsNullOrEmpty(Rating))
            sb.AppendLine($"⭐ Оцінка розмови: {Rating}/10");
        if (!string.IsNullOrEmpty(outcome))
            sb.AppendLine($"✅ До чого дійшли: {outcome}");
        if (!string.IsNullOrEmpty(PaymentProbability))
            sb.AppendLine($"💰 Вірогідність оплати: {PaymentProbability}/10");
        if (!string.IsNullOrWhiteSpace(Note))
            sb.AppendLine($"📝 Примітка: {Note}");

        if (!string.IsNullOrWhiteSpace(CrmUrl))
            sb.AppendLine($"🔗 CRM: {CrmUrl}");

        if (Outcome == "Виставив рахунок" && IsInvoicePaid == true)
            sb.Append("#оплата");

        return sb.ToString().TrimEnd();
    }
}

public class CallReportViewModel : ViewModelBase
{
    public static IReadOnlyList<string> CallTypes { get; } = new[]
    {
        "Перший дзвінок", "Передзвін (перший дзвінок)", "Дзвінок після (подумаю/пораджуся)", "Дзвінок КК", "Недозвін", "Інший"
    };

    public static IReadOnlyList<string> LeadSources { get; } = new[]
    {
        "Лідформа Фейсбук", "Рекомендація", "Лідформа Тікток", "Реактивація", "Вторинне опрацювання", "Листування в соцмережах", "Квіз", "FB + WA", "YouTube", "Кваліфікація Реакт", "Кваліфікація Гаряч", "WA"
    };

    public static IReadOnlyList<string> Ratings { get; } =
        Enumerable.Range(1, 10).Select(i => i.ToString()).ToList();

    public static IReadOnlyList<string> Outcomes { get; } = new[]
    {
       "Виставив рахунок", "Пішов думати", "Потрібно порадитися", "Не підходять умови", "Не цільовий", "Вже не актуально", "Неадекват", "Хоче оплатити в кінці роботи", "Вирішив виїжджати зі США", "Хоче зайнятися пізніше"
    };

    public static IReadOnlyList<string> Positions { get; } = new[]
    {
        "Менеджер", "Діагност", "Кваліфікатор"
    };

    private readonly string _managerName;
    private readonly string _position;
    private string? _selectedCallType;
    private string _customCallType = string.Empty;
    private string? _selectedLeadSource;
    private string? _selectedRating;
    private string? _selectedOutcome;
    private bool _isInvoicePaid;
    private string? _selectedPaymentProbability;
    private string _crmUrl = string.Empty;
    private string _note = string.Empty;

    private readonly Action<CallReportData?> _onComplete;
    private readonly bool _isEditing;

    public string? SelectedCallType
    {
        get => _selectedCallType;
        set
        {
            if (SetField(ref _selectedCallType, value))
            {
                OnPropertyChanged(nameof(IsCustomCallTypeVisible));
                OnPropertyChanged(nameof(IsRatingVisible));
                OnPropertyChanged(nameof(IsOutcomeVisible));
                OnPropertyChanged(nameof(IsNoteVisible));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private bool IsNedozvon => _selectedCallType == "Недозвін";

    public bool IsCustomCallTypeVisible => _selectedCallType == "Інший";
    public bool IsRatingVisible         => !IsNedozvon;
    public bool IsOutcomeVisible        => !IsNedozvon;
    public bool IsNoteVisible           => !IsNedozvon;

    public string CustomCallType
    {
        get => _customCallType;
        set => SetField(ref _customCallType, value);
    }

    public string? SelectedLeadSource
    {
        get => _selectedLeadSource;
        set => SetField(ref _selectedLeadSource, value);
    }

    public string? SelectedRating
    {
        get => _selectedRating;
        set => SetField(ref _selectedRating, value);
    }

    public string? SelectedOutcome
    {
        get => _selectedOutcome;
        set
        {
            if (SetField(ref _selectedOutcome, value))
            {
                OnPropertyChanged(nameof(IsInvoiceCheckboxVisible));
                OnPropertyChanged(nameof(IsPaymentProbabilityVisible));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsInvoiceCheckboxVisible       => _selectedOutcome == "Виставив рахунок";
    public bool IsPaymentProbabilityVisible    => _selectedOutcome == "Виставив рахунок";

    public bool IsInvoicePaid
    {
        get => _isInvoicePaid;
        set => SetField(ref _isInvoicePaid, value);
    }

    public string? SelectedPaymentProbability
    {
        get => _selectedPaymentProbability;
        set => SetField(ref _selectedPaymentProbability, value);
    }

    public string CrmUrl
    {
        get => _crmUrl;
        set
        {
            if (SetField(ref _crmUrl, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    public string SubmitLabel => _isEditing ? "Зберегти" : "Відправити";

    public ICommand SubmitCommand { get; }

    public CallReportViewModel(Action<CallReportData?> onComplete, string? managerName = null, string? position = null, CallReportData? existing = null)
    {
        _onComplete  = onComplete;
        _managerName = managerName ?? string.Empty;
        _position    = position ?? "Менеджер";
        _isEditing   = existing is not null;

        if (existing is not null)
        {
            _position = existing.Position;
            _selectedCallType           = existing.CallType;
            _customCallType             = existing.CustomCallType ?? string.Empty;
            _selectedLeadSource         = existing.LeadSource;
            _selectedRating             = existing.Rating;
            _selectedOutcome            = existing.Outcome;
            _isInvoicePaid              = existing.IsInvoicePaid ?? false;
            _selectedPaymentProbability = existing.PaymentProbability;
            _crmUrl                     = existing.CrmUrl;
            _note                       = existing.Note;
        }

        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);
    }

    private bool CanSubmit() =>
        _selectedCallType   is not null &&
        _selectedLeadSource is not null &&
        (!IsRatingVisible  || _selectedRating  is not null) &&
        (!IsOutcomeVisible || _selectedOutcome is not null) &&
        (!IsPaymentProbabilityVisible || _selectedPaymentProbability is not null) &&
        !string.IsNullOrWhiteSpace(_crmUrl);

    private void OnSubmit() =>
        _onComplete(new CallReportData(
            Manager:            _managerName,
            CallType:           _selectedCallType!,
            CustomCallType:     IsCustomCallTypeVisible ? _customCallType : null,
            LeadSource:         _selectedLeadSource!,
            CrmUrl:             _crmUrl,
            Rating:             IsRatingVisible  ? _selectedRating!  : string.Empty,
            Outcome:            IsOutcomeVisible ? _selectedOutcome! : string.Empty,
            IsInvoicePaid:      IsInvoiceCheckboxVisible ? _isInvoicePaid : null,
            PaymentProbability: IsPaymentProbabilityVisible ? _selectedPaymentProbability! : string.Empty,
            Note:               _note)
        {
            Position = _position,
        });
}
