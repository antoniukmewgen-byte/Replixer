using Replixer.Infrastructure;
using System.Windows.Input;

namespace Replixer.ViewModels.Call;

public class IdleCallViewModel : ViewModelBase
{
    public ICommand RecordManuallyCommand { get; }
    public ICommand ReportMissedCallCommand { get; }

    public IdleCallViewModel(Action onStartRecording, Action onReportMissedCall)
    {
        RecordManuallyCommand   = new RelayCommand(onStartRecording);
        ReportMissedCallCommand = new RelayCommand(onReportMissedCall);
    }
}
