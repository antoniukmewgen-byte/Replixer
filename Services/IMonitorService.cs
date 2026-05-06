namespace EchoVault.Services;

public interface IMonitorService : IDisposable
{
    event Action<string>? CallDetected;
    event Action<string>? CallEnded;
    void Start();
    void Stop();
}
