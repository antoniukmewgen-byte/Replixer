using Replixer.Models;
using Replixer.ViewModels;
using Replixer.ViewModels.Dialogs;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Replixer.Services.Upload;

public class TelegramUploadService : IDisposable
{
    private const int    ApiId   = 12654804;
    private const string ApiHash = "05c29366d6fcc9c48f3778321ad99656";

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "telegram_session.dat");

    public static readonly IReadOnlyList<TelegramChat> Chats = new[]
    {
        new TelegramChat("Test", 3805068290L),
        new TelegramChat("Test2", 3805068290L),
        new TelegramChat("Test3", 3805068290L),
        new TelegramChat("Test4", 3805068290L),
        new TelegramChat("Test5", 3805068290L),
        new TelegramChat("Test6", 3805068290L),
        new TelegramChat("Test7", 3805068290L),
        new TelegramChat("Test8", 3805068290L),
        new TelegramChat("Test9", 3805068290L),
        new TelegramChat("Test10", 3805068290L),
        new TelegramChat("Test11", 3805068290L),
        new TelegramChat("Test12", 3805068290L),
        new TelegramChat("Test13", 3805068290L),
        new TelegramChat("Test14", 3805068290L),


    };

    private readonly AppSettings _settings;
    private WTelegram.Client? _client;
    private string? _pendingPhone;

    public TelegramUploadService(AppSettings settings) => _settings = settings;

    public bool IsAuthorized => File.Exists(SessionPath);

    public void Logout()
    {
        _client?.Dispose();
        _client = null;
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);
        Debug.WriteLine("[TG] Logged out, session deleted");
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<(bool ok, string? error)> AuthorizeAsync(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return (false, "Введіть номер телефону");

        _pendingPhone = NormalizePhone(phone);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            _client?.Dispose();
            _client = new WTelegram.Client(ConfigProvider);
            var user = await _client.LoginUserIfNeeded();
            Debug.WriteLine($"[TG] Authorized as: {user?.username ?? user?.first_name}");
            return user != null ? (true, null) : (false, "Авторизація не вдалася");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] Auth failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return (false, ex.Message);
        }
        finally
        {
            _pendingPhone = null;
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public async Task<bool> SendFileAsync(string filePath, long chatId, string? caption = null)
    {
        Debug.WriteLine($"[TG] ── SendFileAsync ──────────────────────────────");
        Debug.WriteLine($"[TG] File   : {filePath}");
        Debug.WriteLine($"[TG] ChatId : {chatId}");
        try
        {
            await EnsureClientAsync();
            if (_client == null)
            {
                Debug.WriteLine("[TG] ✗ No client — not authorized");
                return false;
            }

            TL.InputPeer? peer = await ResolvePeerAsync(chatId);
            if (peer == null)
                return false;

            Debug.WriteLine($"[TG] Peer resolved: {peer}");
            Debug.WriteLine("[TG] Uploading file…");

            var inputFile = await _client.UploadFileAsync(filePath);
            var fileName  = Path.GetFileName(filePath);

            await _client.SendMediaAsync(peer, caption ?? $"Запис дзвінку: {fileName}", inputFile, "audio/mpeg");

            Debug.WriteLine("[TG] ✓ Sent successfully");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Send failed: {ex.Message}");
            return false;
        }
        finally
        {
            Debug.WriteLine("[TG] ───────────────────────────────────────────");
        }
    }

    private async Task<TL.InputPeer?> ResolvePeerAsync(long chatId)
    {
        // Groups & channels
        var allChats = await _client!.Messages_GetAllChats();
        if (allChats.chats.TryGetValue(chatId, out var chatBase))
            return chatBase;

        Debug.WriteLine($"[TG] Not found in GetAllChats ({allChats.chats.Count} entries), trying GetAllDialogs…");

        // Private chats / bots / users
        var dialogs = await _client.Messages_GetAllDialogs();
        if (dialogs.chats.TryGetValue(chatId, out var dialogChat))
            return dialogChat;
        if (dialogs.users.TryGetValue(chatId, out var user))
            return user;

        Debug.WriteLine($"[TG] ✗ Peer {chatId} not found in any source.");
        Debug.WriteLine($"[TG]   Chats in GetAllChats    : {allChats.chats.Count}");
        Debug.WriteLine($"[TG]   Chats in GetAllDialogs  : {dialogs.chats.Count}");
        Debug.WriteLine($"[TG]   Users in GetAllDialogs  : {dialogs.users.Count}");
        foreach (var (id, c) in allChats.chats)
            Debug.WriteLine($"[TG]   chat  {id} → {c}");
        foreach (var (id, u) in dialogs.users)
            Debug.WriteLine($"[TG]   user  {id} → {u}");
        return null;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task EnsureClientAsync()
    {
        if (_client != null) return;
        if (!IsAuthorized) return;

        try
        {
            _client = new WTelegram.Client(ConfigProvider);
            await _client.LoginUserIfNeeded();
            Debug.WriteLine("[TG] Session restored from file");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Failed to restore session: {ex.Message}");
            _client = null;
        }
    }

    private static string NormalizePhone(string phone)
    {
        var digits = phone.TrimStart('+').Replace(" ", "").Replace("-", "");
        // 0XXXXXXXXX → +380XXXXXXXXX
        if (digits.StartsWith("0") && digits.Length == 10)
            digits = "380" + digits[1..];
        return "+" + digits;
    }

    private string? ConfigProvider(string what) => what switch
    {
        "api_id"            => ApiId.ToString(),
        "api_hash"          => ApiHash,
        "session_pathname"  => SessionPath,
        "phone_number"      => _pendingPhone ?? (string.IsNullOrWhiteSpace(_settings.TelegramPhone) ? null : NormalizePhone(_settings.TelegramPhone)),
        "verification_code" => AskForInput("Введіть код підтвердження з Telegram:"),
        "password"          => AskForInput("Введіть пароль двофакторної авторизації:"),
        _                   => null,
    };

    private static string? AskForInput(string prompt)
    {
        // ConfigProvider is called from a WTelegramClient background thread.
        // We post the overlay to the UI thread, then block the background thread
        // on the TCS until the user confirms or cancels.
        var tcs = new TaskCompletionSource<string?>();
        Application.Current.Dispatcher.Invoke(() =>
        {
            var host = (IDialogHost)Application.Current.MainWindow.DataContext;
            var vm = new InputDialogViewModel(prompt, result =>
            {
                host.HideInputDialog();
                tcs.TrySetResult(result);
            });
            host.ShowInputDialog(vm);
        });
        return tcs.Task.GetAwaiter().GetResult();
    }
}
