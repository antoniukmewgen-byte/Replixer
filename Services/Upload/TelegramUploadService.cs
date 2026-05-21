using Replixer.Models;
using Replixer.ViewModels;
using Replixer.ViewModels.Dialogs;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TL;

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
        new TelegramChat("TestGroup", 3805068290L),

        new TelegramChat("Чат Kvalifikatory Team", 3836828860L,5),
        new TelegramChat("Записи разговоров Тим лидов", 3688506342L),
        new TelegramChat("Записи разговоров Адама", 3891343034L),
        new TelegramChat("Стажування Move Nation", 3600976908L,261),
        new TelegramChat("Чат Avangard Team", 3865749650L,2),
        new TelegramChat("Чат Prime Team", 3991400384L,7),
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

    public async Task<int?> SendFileAsync(string filePath, long chatId, int? topicId = null, string? caption = null, string? driveUrl = null)
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
                return null;
            }

            TL.InputPeer? peer = await ResolvePeerAsync(chatId);
            if (peer == null)
                return null;

            Debug.WriteLine($"[TG] Peer resolved: {peer}");
            Debug.WriteLine("[TG] Uploading file…");

            var inputFile = await _client.UploadFileAsync(filePath);
            var fileName  = Path.GetFileName(filePath);

            var (msgText, entities) = BuildCaption(caption ?? $"Запис дзвінку: {fileName}", driveUrl);
            var msg = await _client.SendMediaAsync(peer, msgText, inputFile, "audio/mpeg", entities: entities, reply_to_msg_id: topicId ?? 0);

            Debug.WriteLine($"[TG] ✓ Sent successfully, messageId={msg?.id}");
            return msg?.id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Send failed: {ex.Message}");
            return null;
        }
        finally
        {
            Debug.WriteLine("[TG] ───────────────────────────────────────────");
        }
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    public async Task<bool> EditMessageAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null)
    {
        Debug.WriteLine($"[TG] ── EditMessageAsync (msgId={messageId}) ───────");
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

            var (msgText, entities) = BuildCaption(caption, driveUrl);
            await _client.Messages_EditMessage(peer, messageId, message: msgText, entities: entities);
            Debug.WriteLine("[TG] ✓ Edited successfully");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Edit failed: {ex.Message}");
            return false;
        }
        finally
        {
            Debug.WriteLine("[TG] ───────────────────────────────────────────");
        }
    }

    private static (string text, TL.MessageEntity[]? entities) BuildCaption(string caption, string? driveUrl)
    {
        if (string.IsNullOrWhiteSpace(driveUrl))
            return (caption, null);

        // Pull trailing hashtag lines out so they go after the Drive link
        var trimmed   = caption.TrimEnd();
        var lastNl    = trimmed.LastIndexOf('\n');
        var body      = trimmed;
        var tagSuffix = string.Empty;
        if (lastNl >= 0)
        {
            var lastLine = trimmed[(lastNl + 1)..].TrimStart('\r');
            if (lastLine.StartsWith('#'))
            {
                body      = trimmed[..lastNl].TrimEnd();
                tagSuffix = "\n" + lastLine;
            }
        }

        var text = body + $"\n💾 Google Drive: {driveUrl}" + tagSuffix;
        return (text, null);
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
