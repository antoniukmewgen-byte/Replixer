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
        sb.AppendLine($"👤 Менеджер: {Manager}");
        sb.AppendLine($"📞 Тип дзвінка: {callType}");
        sb.AppendLine($"📣 Джерело ліда: {LeadSource}");
        sb.AppendLine($"⭐ Оцінка розмови: {Rating}/10");
        sb.AppendLine($"✅ До чого дійшли: {outcome}");
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
        "Перший дзвінок", "Передзвін (перший дзвінок)", "Дзвінок після (подумаю/пораджуся)", "Дзвінок КК", "Інший"
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

    private readonly string _managerName;
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
                OnPropertyChanged(nameof(IsCustomCallTypeVisible));
        }
    }

    public bool IsCustomCallTypeVisible => _selectedCallType == "Інший";

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
                OnPropertyChanged(nameof(IsInvoiceCheckboxVisible));
        }
    }

    public bool IsInvoiceCheckboxVisible => _selectedOutcome == "Виставив рахунок";

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

    public CallReportViewModel(Action<CallReportData?> onComplete, string? managerName = null, CallReportData? existing = null)
    {
        _onComplete  = onComplete;
        _managerName = managerName ?? string.Empty;
        _isEditing   = existing is not null;

        if (existing is not null)
        {
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
        _selectedCallType           is not null &&
        _selectedLeadSource         is not null &&
        _selectedRating             is not null &&
        _selectedOutcome            is not null &&
        _selectedPaymentProbability is not null &&
        !string.IsNullOrWhiteSpace(_crmUrl);

    private void OnSubmit() =>
        _onComplete(new CallReportData(
            Manager:            _managerName,
            CallType:           _selectedCallType!,
            CustomCallType:     IsCustomCallTypeVisible ? _customCallType : null,
            LeadSource:         _selectedLeadSource!,
            CrmUrl:             _crmUrl,
            Rating:             _selectedRating!,
            Outcome:            _selectedOutcome!,
            IsInvoicePaid:      IsInvoiceCheckboxVisible ? _isInvoicePaid : null,
            PaymentProbability: _selectedPaymentProbability!,
            Note:               _note));
}
