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

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "error_queue.json");

    private static readonly string _appVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public static void Configure(AppSettings settings)
        => _userName = settings.ManagerName.Trim() is { Length: > 0 } n ? n : "Unknown";

    // Normal (recoverable) errors — fire and forget
    public static void Report(string category, string message, Exception? ex = null)
        => _ = SendOrQueueAsync(CreateEntry(category, message, ex));

    // Crash handlers — synchronously write to queue first so it survives process death
    public static void ReportCrash(string category, string message, Exception? ex = null)
    {
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
            if (string.IsNullOrEmpty(token) || chatId == 0) return true; // not configured

            var text    = FormatMessage(entry);
            var payload = JsonSerializer.Serialize(new { chat_id = chatId, text });

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.telegram.org/bot{token}/sendMessage")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var res = await _http.SendAsync(req);
            Debug.WriteLine($"[ErrorReporter] {(int)res.StatusCode}");
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ErrorReporter] Send failed: {ex.Message}");
            return false;
        }
    }

    private static string FormatMessage(ErrorEntry e)
    {
        var sb   = new StringBuilder();
        var icon = e.Category.StartsWith("CRASH") ? "💥" : "⚠️";

        sb.AppendLine($"{icon} {e.Category}");
        sb.AppendLine($"👤 {e.User}  |  v{e.AppVersion}");
        sb.AppendLine($"🕐 {e.Timestamp:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(e.Message);

        if (e.Detail is { Length: > 0 })
        {
            sb.AppendLine();
            var detail = e.Detail.Length > 3000 ? e.Detail[..3000] + "\n…(обрізано)" : e.Detail;
            sb.Append(detail);
        }

        return sb.ToString().TrimEnd();
    }

    private static ErrorEntry CreateEntry(string category, string message, Exception? ex) => new()
    {
        Timestamp  = DateTime.Now,
        User       = _userName,
        AppVersion = _appVersion,
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
