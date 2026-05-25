namespace Replixer.Services;

public enum NotificationType { Success, Error }

public static class NotificationService
{
    public static event Action<string, NotificationType>? Raised;

    public static void ShowSuccess(string message) => Raise(message, NotificationType.Success);
    public static void ShowError(string message)   => Raise(message, NotificationType.Error);

    private static void Raise(string message, NotificationType type)
        => Raised?.Invoke(message, type);
}
