using Replixer.Models;
using Replixer.Views;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Replixer.Services.Upload;

public class TelegramUploadService
{
    private const int    ApiId   = 12654804;
    private const string ApiHash = "05c29366d6fcc9c48f3778321ad99656";

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "telegram_session.dat");

    public static readonly IReadOnlyList<TelegramChat> Chats = new[]
    {
        new TelegramChat("Test", 3805068290L),
    };

    private readonly AppSettings _settings;
    private WTelegram.Client? _client;
    private string? _pendingPhone;

    public TelegramUploadService(AppSettings settings) => _settings = settings;

    public bool IsAuthorized => File.Exists(SessionPath);

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<bool> AuthorizeAsync(string phone)
    {
        _pendingPhone = phone;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            _client?.Dispose();
            _client = new WTelegram.Client(ConfigProvider);
            var user = await _client.LoginUserIfNeeded();
            Debug.WriteLine($"[TG] Authorized as: {user?.username ?? user?.first_name}");
            return user != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] Auth failed: {ex.Message}");
            _client = null;
            return false;
        }
        finally
        {
            _pendingPhone = null;
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public async Task<bool> SendFileAsync(string filePath, long chatId)
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

            await _client.SendMediaAsync(peer, $"Запис дзвінку: {fileName}", inputFile, "audio/mpeg");

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

    private string? ConfigProvider(string what) => what switch
    {
        "api_id"            => ApiId.ToString(),
        "api_hash"          => ApiHash,
        "session_pathname"  => SessionPath,
        "phone_number"      => _pendingPhone ?? _settings.TelegramPhone,
        "verification_code" => AskForInput("Введіть код підтвердження з Telegram:"),
        "password"          => AskForInput("Введіть пароль двофакторної авторизації:"),
        _                   => null,
    };

    private static string? AskForInput(string prompt)
    {
        string? result = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = new InputWindow(prompt);
            win.ShowDialog();
            result = win.Result;
        });
        return result;
    }
}
