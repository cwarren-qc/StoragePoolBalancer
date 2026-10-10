namespace StorageBalancer.App.Subsystems.Logging;

public interface IAppEventLogger
{
    void LogInfo(string category, string message);
    void LogWarning(string category, string message);
    void LogError(string category, string message, Exception? exception = null);
}
