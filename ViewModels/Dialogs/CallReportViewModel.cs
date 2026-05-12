using Replixer.Infrastructure;
using System.Text;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public record CallReportData(
    string Manager,
    string CallType,
    string? CustomCallType,
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
        if (!string.IsNullOrWhiteSpace(CrmUrl))
            sb.AppendLine($"🔗 CRM: {CrmUrl}");
        sb.AppendLine($"⭐ Оцінка розмови: {Rating}/10");
        sb.AppendLine($"✅ До чого дійшли: {outcome}");
        sb.AppendLine($"💰 Вірогідність оплати: {PaymentProbability}/10");
        if (!string.IsNullOrWhiteSpace(Note))
            sb.Append($"📝 Примітка: {Note}");

        return sb.ToString().TrimEnd();
    }
}

public class CallReportViewModel : ViewModelBase
{
    public static IReadOnlyList<string> CallTypes { get; } = new[]
    {
        "Вхідний", "Вихідний", "Пропущений", "Холодний", "Інший"
    };

    public static IReadOnlyList<string> LeadSources { get; } = new[]
    {
        "Instagram", "Facebook", "Сайт", "Рекомендація", "Google", "Ringostat", "Інше"
    };

    public static IReadOnlyList<string> Ratings { get; } =
        Enumerable.Range(1, 10).Select(i => i.ToString()).ToList();

    public static IReadOnlyList<string> Outcomes { get; } = new[]
    {
        "Відмова", "Потребує часу", "Передзвонимо", "Виставив рахунок", "Повторна покупка", "Інше"
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
        set => SetField(ref _crmUrl, value);
    }

    public string Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    public string SubmitLabel => "Відправити";

    public ICommand SubmitCommand { get; }

    public CallReportViewModel(Action<CallReportData?> onComplete, string? managerName = null)
    {
        _onComplete   = onComplete;
        _managerName  = managerName ?? string.Empty;
        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);
    }

    private bool CanSubmit() =>
        _selectedCallType           is not null &&
        _selectedLeadSource         is not null &&
        _selectedRating             is not null &&
        _selectedOutcome            is not null &&
        _selectedPaymentProbability is not null;

    private void OnSubmit() =>
        _onComplete(new CallReportData(
            Manager:             _managerName,
            CallType:            _selectedCallType!,
            CustomCallType:      IsCustomCallTypeVisible ? _customCallType : null,
            CrmUrl:              _crmUrl,
            Rating:              _selectedRating!,
            Outcome:             _selectedOutcome!,
            IsInvoicePaid:       IsInvoiceCheckboxVisible ? _isInvoicePaid : null,
            PaymentProbability:  _selectedPaymentProbability!,
            Note:                _note));
}
