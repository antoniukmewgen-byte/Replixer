using EchoVault.ViewModels.Call;

namespace EchoVault.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private ViewModelBase _callContent;
    public ViewModelBase CallContent
    {
        get => _callContent;
        private set => SetField(ref _callContent, value);
    }

    public HomeViewModel()
    {
        _callContent = new IdleCallViewModel(StartRecording);
    }

    private void StartRecording()
    {
        CallContent = new ActiveCallViewModel(StopRecording);
    }

    private void StopRecording()
    {
        CallContent = new IdleCallViewModel(StartRecording);
    }
}
