using Replixer.Infrastructure;
using Replixer.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Replixer.Services;

internal static class ErrorReporter
{
    private static string _userName = "Unknown";
    private static AppSettings? _settings;
    private static Timer? _retryTimer;

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "error_queue.json");

    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public static void Configure(AppSettings settings)
    {
        _settings   = settings;
        _userName   = settings.ManagerName.Trim() is { Length: > 0 } n ? n : "Unknown";
        _retryTimer = new Timer(_ => _ = FlushQueueAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // Normal (recoverable) errors — fire and forget
    public static void Report(string category, string message, Exception? ex = null)
    {
        NotificationService.ShowError(message);
        _ = SendOrQueueAsync(CreateEntry(category, message, ex));
    }

    // Crash handlers — synchronously write to queue first so it survives process death
    public static void ReportCrash(string category, string message, Exception? ex = null)
    {
        NotificationService.ShowError("Сталася критична помилка. Звіт надіслано розробнику.");
        var entry = CreateEntry(category, message, ex);
        AppendToQueue(entry);    // Sync write — guaranteed even if process dies immediately
        _ = TrySendAsync(entry); // Best-effort send
    }

    public static async Task FlushQueueAsync()
    {
        var queued = LoadQueue();
        if (queued.Count == 0) return;

        var sent = 0;
        foreach (var entry in queued)
        {
            if (!await TrySendAsync(entry)) break;
            sent++;
        }

        SaveQueue(queued.Skip(sent).ToList());
    }

    private static async Task SendOrQueueAsync(ErrorEntry entry)
    {
        if (!await TrySendAsync(entry))
            AppendToQueue(entry);
    }

    private static async Task<bool> TrySendAsync(ErrorEntry entry)
    {
        try
        {
            var token  = AppSecrets.ErrorBotToken;
            var chatId = AppSecrets.ErrorChatId;
            if (string.IsNullOrEmpty(token) || chatId == 0) return true;

            var chunks = SplitIntoChunks(FormatMessage(entry), maxLength: 4096);
            foreach (var chunk in chunks)
            {
                var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = chunk });
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"https://api.telegram.org/bot{token}/sendMessage")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                var res = await _http.SendAsync(req);
                Debug.WriteLine($"[ErrorReporter] {(int)res.StatusCode}");
                if (!res.IsSuccessStatusCode) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ErrorReporter] Send failed: {ex.Message}");
            return false;
        }
    }

    private static List<string> SplitIntoChunks(string text, int maxLength)
    {
        var chunks = new List<string>();
        var start  = 0;
        while (start < text.Length)
        {
            var len = Math.Min(maxLength, text.Length - start);
            // break at newline boundary if possible
            if (start + len < text.Length)
            {
                var nl = text.LastIndexOf('\n', start + len - 1, len);
                if (nl > start) len = nl - start + 1;
            }
            chunks.Add(text.Substring(start, len));
            start += len;
        }
        return chunks;
    }

    private static string FormatMessage(ErrorEntry e)
    {
        var sb   = new StringBuilder();
        var icon = e.Category.StartsWith("CRASH") ? "💥" : "⚠️";

        sb.AppendLine($"{icon} {e.Category}");
        sb.AppendLine($"👤 {e.User}  |  v{e.AppVersion}");
        sb.AppendLine($"🕐 {e.Timestamp:dd.MM.yyyy HH:mm:ss}");

        if (_settings is { } s)
        {
            sb.AppendLine();
            sb.AppendLine(FormatIntegrations(s));
        }

        sb.AppendLine();
        sb.AppendLine(e.Message);

        if (e.Detail is { Length: > 0 })
        {
            sb.AppendLine();
            sb.Append(e.Detail);
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatIntegrations(AppSettings s)
    {
        static string Status(bool enabled, bool? connected) =>
            !enabled ? "вимк." : connected == true ? "✅" : connected == false ? "❌" : "⏳";

        var parts = new[]
        {
            $"Google Drive: {Status(s.IsGoogleDriveEnabled, s.IsGoogleDriveConnected)}",
            $"Telegram: {Status(s.IsTelegramEnabled, s.IsTelegramConnected)}" +
                (s.IsTelegramEnabled && !string.IsNullOrEmpty(s.TelegramPhone) ? $" ({s.TelegramPhone})" : ""),
            $"Kommo: {Status(s.IsKommoEnabled, s.IsKommoConnected)}" +
                (s.IsKommoEnabled && !string.IsNullOrEmpty(s.KommoSubdomain) ? $" ({s.KommoSubdomain})" : ""),
        };
        return "🔌 " + string.Join("  |  ", parts);
    }

    private static ErrorEntry CreateEntry(string category, string message, Exception? ex) => new()
    {
        Timestamp  = DateTime.Now,
        User       = _userName,
        AppVersion = AppVersion,
        Category   = category,
        Message    = message,
        Detail     = ex?.ToString()
    };

    private static void AppendToQueue(ErrorEntry entry)
    {
        try
        {
            var list = LoadQueue();
            if (list.Count >= 50) list.RemoveAt(0); // cap at 50 entries
            list.Add(entry);
            SaveQueue(list);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ErrorReporter] Queue write failed: {ex.Message}");
        }
    }

    private static List<ErrorEntry> LoadQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return [];
            return JsonSerializer.Deserialize<List<ErrorEntry>>(
                File.ReadAllText(QueuePath)) ?? [];
        }
        catch { return []; }
    }

    private static void SaveQueue(List<ErrorEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(entries));
        }
        catch { }
    }
}

internal sealed record ErrorEntry
{
    public DateTime Timestamp  { get; init; }
    public string   User       { get; init; } = "";
    public string   AppVersion { get; init; } = "";
    public string   Category   { get; init; } = "";
    public string   Message    { get; init; } = "";
    public string?  Detail     { get; init; }
}
