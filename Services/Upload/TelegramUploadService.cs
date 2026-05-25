using Replixer.Infrastructure;
using Replixer.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TL;

namespace Replixer.Services.Upload;

public class TelegramUploadService : IDisposable
{
    private static int    ApiId   => AppSecrets.TelegramApiId;
    private static string ApiHash => AppSecrets.TelegramApiHash;

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "telegram_session.dat");

    public static IReadOnlyList<TelegramChat> Chats => AppSecrets.TelegramChats;

    private readonly AppSettings _settings;
    private WTelegram.Client? _client;
    private string? _pendingPhone;

    public Func<string, Task<string?>>? InputHandler { get; set; }

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

    public async Task<string?> EditMessageAsync(int messageId, long chatId, int? topicId, string caption, string? driveUrl = null)
    {
        Debug.WriteLine($"[TG] ── EditMessageAsync (msgId={messageId}) ───────");
        try
        {
            await EnsureClientAsync();
            if (_client == null)
            {
                Debug.WriteLine("[TG] ✗ No client — not authorized");
                return "Telegram: не авторизовано";
            }

            TL.InputPeer? peer = await ResolvePeerAsync(chatId);
            if (peer == null)
                return "Telegram: не вдалося знайти чат";

            var (msgText, entities) = BuildCaption(caption, driveUrl);
            await _client.Messages_EditMessage(peer, messageId, message: msgText, entities: entities);
            Debug.WriteLine("[TG] ✓ Edited successfully");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Edit failed: {ex.Message}");
            return $"Telegram: {ex.Message}";
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

        var (body, hashtagLine) = CaptionHelper.SplitHashtagSuffix(caption);
        var text = body + $"\n💾 Google Drive: {driveUrl}"
                       + (hashtagLine is null ? string.Empty : "\n" + hashtagLine);
        return (text, null);
    }

    private async Task<TL.InputPeer?> ResolvePeerAsync(long chatId)
    {
        var allChats = await _client!.Messages_GetAllChats();
        if (allChats.chats.TryGetValue(chatId, out var chatBase))
            return chatBase;

        Debug.WriteLine($"[TG] Not found in GetAllChats ({allChats.chats.Count} entries), trying GetAllDialogs…");

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

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

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

    private string? AskForInput(string prompt)
    {
        var handler = InputHandler;
        if (handler is null)
        {
            Debug.WriteLine("[TG] ✗ No InputHandler registered — cannot request user input");
            return null;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Debug.WriteLine("[TG] ✗ Dispatcher unavailable — skipping user input");
            return null;
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var op = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var result = await handler(prompt);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TG] InputHandler threw: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        op.Task.ContinueWith(
            t => tcs.TrySetResult(null),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        if (!tcs.Task.Wait(TimeSpan.FromMinutes(3)))
        {
            Debug.WriteLine("[TG] ✗ AskForInput timed out after 3 minutes");
            tcs.TrySetCanceled();
            return null;
        }

        return tcs.Task.Result;
    }
}
