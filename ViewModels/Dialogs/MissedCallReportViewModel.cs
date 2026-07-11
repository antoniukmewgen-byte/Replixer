using Replixer.Infrastructure;
using System.Text;
using System.Windows.Input;

namespace Replixer.ViewModels.Dialogs;

public record MissedCallReportData(
    string Manager,
    string CallType,
    string LeadSource,
    string CrmUrl,
    string Note)
{
    public bool IsNoLink { get; init; } = false;


    public string FormatCaption()
    {
        var sb = new StringBuilder();
        sb.AppendLine("📋 Звіт по дзвінку");
        sb.AppendLine();
        sb.AppendLine($"👤 Менеджер: {Manager}");
        sb.AppendLine($"📞 Тип дзвінка: {CallType}");
        if (!string.IsNullOrWhiteSpace(LeadSource))
            sb.AppendLine($"📣 Джерело ліда: {LeadSource}");
        if (!string.IsNullOrWhiteSpace(Note))
            sb.AppendLine($"📝 Примітка: {Note}");
        if (!string.IsNullOrWhiteSpace(CrmUrl))
            sb.AppendLine($"🔗 CRM: {CrmUrl}");

        return sb.ToString().TrimEnd();
    }
}

public class MissedCallReportViewModel : ViewModelBase
{
    public static IReadOnlyList<string> CallTypes { get; } = new[]
    {
        "Недодзвон (ще не було спілкування)", "Недодзвон (вже було спілкування)"
    };

    public static IReadOnlyList<string> LeadSources => CallReportViewModel.LeadSources;

    private readonly string _managerName;
    private string? _selectedCallType;
    private string? _selectedLeadSource;
    private bool _isNoLink;
    private string _crmUrl = string.Empty;
    private string _note = string.Empty;

    private readonly Action<MissedCallReportData?> _onComplete;

    public string? SelectedCallType
    {
        get => _selectedCallType;
        set
        {
            if (SetField(ref _selectedCallType, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? SelectedLeadSource
    {
        get => _selectedLeadSource;
        set
        {
            if (SetField(ref _selectedLeadSource, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsNoLink
    {
        get => _isNoLink;
        set
        {
            if (SetField(ref _isNoLink, value))
            {
                OnPropertyChanged(nameof(IsCrmUrlVisible));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsCrmUrlVisible => !_isNoLink;

    public string CrmUrl
    {
        get => _crmUrl;
        set
        {
            if (SetField(ref _crmUrl, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public const int NoteMaxLength = 500;

    public string Note
    {
        get => _note;
        set
        {
            if (SetField(ref _note, value))
            {
                OnPropertyChanged(nameof(NoteLength));
                OnPropertyChanged(nameof(IsNoteNearLimit));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int  NoteLength      => _note.Length;
    public bool IsNoteNearLimit => _note.Length >= NoteMaxLength - 50;

    public ICommand SubmitCommand { get; }

    public MissedCallReportViewModel(Action<MissedCallReportData?> onComplete, string? managerName = null)
    {
        _onComplete   = onComplete;
        _managerName  = managerName ?? string.Empty;
        SubmitCommand = new RelayCommand(OnSubmit, CanSubmit);
    }

    private bool CanSubmit() =>
        _selectedCallType   is not null &&
        _selectedLeadSource is not null &&
        (_isNoLink || !string.IsNullOrWhiteSpace(_crmUrl)) &&
        !string.IsNullOrWhiteSpace(_note);

    private void OnSubmit() =>
        _onComplete(new MissedCallReportData(
            Manager:    _managerName,
            CallType:   _selectedCallType!,
            LeadSource: _selectedLeadSource!,
            CrmUrl:     _isNoLink ? string.Empty : _crmUrl,
            Note:       _note)
        {
            IsNoLink = _isNoLink,
        });
}
