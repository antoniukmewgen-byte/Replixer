namespace Replixer.Services.Manager
{
    public interface IWindowManager
    {
        void ShowCheatSheet(IEnumerable<string> items);
        void CloseCheatSheet();
    }
}
