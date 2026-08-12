using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TL;

namespace Replixer.Services.Upload;

public class TelegramUploadService : IDisposable
{
    // Повертається з EditMessageAsync, коли Telegram відповідає MESSAGE_ID_INVALID — це означає,
    // що повідомлення з таким msgId більше не існує в чаті (найімовірніше, його видалили вручну),
    // а не транзиентну мережеву/API помилку. Викликач (HomeViewModel.EditEntryReportAsync)
    // звіряється з цим маркером, щоб скинути entry.TelegramMessageId — інакше кожна наступна
    // спроба редагування звіту знову впиралась би в ту саму помилку назавжди.
    public const string MessageDeletedWarning = "Telegram: повідомлення видалено з чату (можливо, його прибрали вручну)";

    private static int    ApiId   => AppSecrets.TelegramApiId;
    private static string ApiHash => AppSecrets.TelegramApiHash;

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "telegram_session.dat");

    public static IReadOnlyList<TelegramChat> Chats => AppSecrets.TelegramChats;

    private readonly AppSettings _settings;
    private WTelegram.Client? _client;
    private string? _pendingPhone;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // Cached after first successful ResolvePeerAsync — avoids two heavy API calls per upload.
    private TL.InputPeer? _cachedPeer;
    private long          _cachedPeerForChatId;

    // True only when the client is actually connected and session restored, not just when
    // the session file exists. Updated atomically inside the client lock.
    private volatile bool _isReady;

    public Func<string, Task<string?>>? InputHandler { get; set; }

    /// <summary>
    /// Fired when the remote session is revoked (AUTH_KEY_UNREGISTERED).
    /// Subscribers should reset their "connected" state and prompt re-auth.
    /// </summary>
    public event Action? SessionInvalidated;

    public TelegramUploadService(AppSettings settings) => _settings = settings;

    /// <summary>
    /// True when the MTProto session is established and the client is ready to send.
    /// More reliable than checking only for the session file — the file can exist while
    /// the client is null (before the first EnsureClientAsync) or already disposed.
    /// </summary>
    public bool IsReady => _isReady;

    // Keep backward-compatible property so callers that only care about "was ever logged in"
    // can still check file existence (used by ProfileViewModel initial state restore).
    public bool IsAuthorized => File.Exists(SessionPath);

    public void Logout()
    {
        _isReady = false;
        InvalidatePeerCache();
        _client?.Dispose();
        _client = null;
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);
        Debug.WriteLine("[TG] Logged out, session deleted");
    }

    // Called when Telegram returns 401 AUTH_KEY_UNREGISTERED — the session was
    // revoked remotely (user terminated it in Telegram settings or it expired).
    // We wipe the local session so IsAuthorized becomes false and the settings
    // page shows the re-auth prompt on next open.
    private void HandleAuthKeyUnregistered()
    {
        _isReady = false;
        InvalidatePeerCache();
        _client?.Dispose();
        _client = null;
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);

        Debug.WriteLine("[TG] Session invalidated due to AUTH_KEY_UNREGISTERED");
        ErrorReporter.Report("TELEGRAM", "Сесія Telegram анульована. Потрібна повторна авторизація у налаштуваннях.");
        SessionInvalidated?.Invoke();
    }

    private void InvalidatePeerCache()
    {
        _cachedPeer          = null;
        _cachedPeerForChatId = 0;
    }

    public async Task<(bool ok, string? error)> AuthorizeAsync(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return (false, "Введіть номер телефону");

        _pendingPhone = NormalizePhone(phone);

        // Serialize against EnsureClientAsync — both construct a WTelegram.Client bound to
        // the same session file (telegram_session.dat). WTelegram opens that file exclusively,
        // so if a background retry (EnsureClientAsync) and a manual re-auth (this method) race
        // to construct a client at the same time, the loser gets
        // "IOException: file is being used by another process". Sharing _clientLock makes the
        // second caller simply wait its turn instead of colliding.
        await _clientLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            _isReady = false;
            InvalidatePeerCache();
            _client?.Dispose();

            // Завжди починаємо з чистого файлу сесії. Якщо попередня спроба логіну впала
            // посеред хендшейку (напр. через тимчасово зламаний api_hash — див. v1.5.8/1.5.9),
            // DpapiSessionStream.Write() вже міг встигнути персистнути на диск частковий/сміттєвий
            // стан сесії (Persist() викликається на кожен Write(), а не лише по завершенню логіну).
            // Без видалення тут новий WTelegram.Client підхопив би саме ці биті дані замість
            // чистого старту, і повторна авторизація користувача не допомагала б.
            if (File.Exists(SessionPath))
                File.Delete(SessionPath);

            _client = new WTelegram.Client(ConfigProvider, new DpapiSessionStream(SessionPath));
            var user = await RunOnDedicatedThreadAsync(() => _client.LoginUserIfNeeded());
            _isReady = user != null;
            Debug.WriteLine($"[TG] Authorized as: {user?.username ?? user?.first_name}");
            return user != null ? (true, null) : (false, "Авторизація не вдалася");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] Auth failed: {ex.Message}");
            _isReady = false;
            _client?.Dispose();
            _client = null;
            return (false, ex.Message);
        }
        finally
        {
            _clientLock.Release();
            _pendingPhone = null;
        }
    }

    public Task<int?> SendFileAsync(string filePath, long chatId, int? topicId = null, string? caption = null, string? driveUrl = null, string? mimeType = null)
        => SendFileCoreAsync(filePath, chatId, topicId, caption, driveUrl, mimeType, isRetry: false);

    private async Task<int?> SendFileCoreAsync(string filePath, long chatId, int? topicId, string? caption, string? driveUrl, string? mimeType, bool isRetry)
    {
        Debug.WriteLine($"[TG] ── SendFileAsync{(isRetry ? " (retry)" : "")} ──────────────────────────────");
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
            {
                ErrorReporter.Report("TELEGRAM", $"Чат не знайдений (chatId={chatId})");
                return null;
            }

            Debug.WriteLine($"[TG] Peer resolved: {peer}");
            Debug.WriteLine("[TG] Uploading file…");

            var fileName = Path.GetFileName(filePath);
            var (msgText, entities) = BuildCaption(caption ?? $"Запис дзвінку: {fileName}", driveUrl);

            var client = _client;

            // UploadFileAsync/SendMediaAsync go straight over WTelegram's own MTProto socket —
            // there's no HttpClient underneath, so no built-in timeout (same class of issue as
            // LoginUserIfNeeded — see the 30s cap in EnsureClientAsync above). If the network
            // stalls mid-transfer (Wi-Fi hiccup, ISP blip) the socket just sits waiting for a
            // server ACK indefinitely — no exception is ever thrown, which freezes the whole
            // UploadOrchestrator chain (Telegram runs before Kommo) and leaves the recording
            // stuck on "Завантаження..." until the app is force-restarted. Cap the whole
            // upload+send step at 120s (generous — includes actual file transfer, unlike the
            // 30s login cap) so a stall surfaces as a normal failure instead of an infinite hang.
            async Task<int?> SendCoreAsync()
            {
                var inputFile = await client.UploadFileAsync(filePath);
                var sentMsg   = await client.SendMediaAsync(peer, msgText, inputFile, mimeType ?? "audio/mpeg", entities: entities, reply_to_msg_id: topicId ?? 0);
                return sentMsg?.id;
            }

            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var sendTask  = SendCoreAsync();
            var completed = await Task.WhenAny(sendTask, Task.Delay(Timeout.Infinite, sendCts.Token));

            if (completed != sendTask)
            {
                Debug.WriteLine("[TG] ✗ Upload/send timed out after 120 s — network likely unstable");
                ErrorReporter.Report("TELEGRAM", "Відправка файлу в Telegram перевищила 120 с — ймовірно, нестабільне з'єднання.");
                return null;
            }

            int? msgId = await sendTask; // propagate any exception from the completed task
            Debug.WriteLine($"[TG] ✓ Sent successfully, messageId={msgId}");
            return msgId;
        }
        catch (TL.RpcException ex) when (ex.Code == 401)
        {
            Debug.WriteLine($"[TG] ✗ Auth key unregistered — invalidating session");
            HandleAuthKeyUnregistered();
            return null;
        }
        catch (NullReferenceException ex) when (!isRetry)
        {
            // WTelegram's internal DC connection became null (network drop or session restored
            // before DC map was populated). Reset client, wait for DC init, then retry once.
            Debug.WriteLine($"[TG] ✗ WTelegram internal null (DC client lost) — resetting and retrying: {ex.Message}");
            _isReady = false;
            InvalidatePeerCache();
            _client?.Dispose();
            _client = null;
            await Task.Delay(2000);
            return await SendFileCoreAsync(filePath, chatId, topicId, caption, driveUrl, mimeType, isRetry: true);
        }
        catch (TaskCanceledException ex) when (!isRetry)
        {
            // WTelegram's internal RPC got canceled mid-request — we never pass a CancellationToken
            // into SendMediaAsync ourselves, so this is caused by the underlying connection dropping
            // (Wi-Fi switch, sleep/wake, DC reconnect), not by any code-level cancellation.
            // Reset the client so the retry reconnects cleanly, then retry once.
            Debug.WriteLine($"[TG] ✗ Send RPC canceled (network drop?) — resetting and retrying: {ex.Message}");
            _isReady = false;
            InvalidatePeerCache();
            _client?.Dispose();
            _client = null;
            await Task.Delay(2000);
            return await SendFileCoreAsync(filePath, chatId, topicId, caption, driveUrl, mimeType, isRetry: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Send failed: {ex.Message}");
            ErrorReporter.Report("TELEGRAM", $"SendFile failed: {ex.Message}", ex);
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
        catch (TL.RpcException ex) when (ex.Code == 401)
        {
            Debug.WriteLine($"[TG] ✗ Auth key unregistered — invalidating session");
            HandleAuthKeyUnregistered();
            return "Telegram: сесія анульована, потрібна повторна авторизація";
        }
        catch (TL.RpcException ex) when (ex.Message == "MESSAGE_ID_INVALID")
        {
            Debug.WriteLine("[TG] ✗ Message no longer exists (likely deleted from chat)");
            return MessageDeletedWarning;
        }
        catch (TL.RpcException ex) when (ex.Message == "MESSAGE_NOT_MODIFIED")
        {
            // Telegram кидає це, коли новий текст побайтово збігається зі старим — тобто
            // редагувати нічого не треба, повідомлення вже показує актуальний контент.
            // Це м'який успіх, а не помилка: раніше він потрапляв у загальний catch нижче
            // й помилково показував користувачу "Не вдалося оновити звіт", хоча по факту
            // все вже було в потрібному стані (типовий сценарій — форма збережена без змін,
            // що впливають на сформований caption).
            Debug.WriteLine("[TG] ✓ Message already matches new content — nothing to edit");
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
        var version = $"\n🔖 v{AppVersion}";

        if (string.IsNullOrWhiteSpace(driveUrl))
            return (caption + version, null);

        var (body, hashtagLine) = CaptionHelper.SplitHashtagSuffix(caption);
        var text = body + $"\n💾 Google Drive: {driveUrl}"
                       + (hashtagLine is null ? string.Empty : "\n" + hashtagLine)
                       + version;
        return (text, null);
    }

    private static string AppVersion => ErrorReporter.AppVersion;

    private async Task<TL.InputPeer?> ResolvePeerAsync(long chatId)
    {
        if (_cachedPeerForChatId == chatId && _cachedPeer is not null)
        {
            Debug.WriteLine($"[TG] Peer resolved from cache: {_cachedPeer}");
            return _cachedPeer;
        }

        var allChats = await _client!.Messages_GetAllChats();
        if (allChats.chats.TryGetValue(chatId, out var chatBase))
        {
            _cachedPeer          = chatBase;
            _cachedPeerForChatId = chatId;
            return chatBase;
        }

        Debug.WriteLine($"[TG] Not found in GetAllChats ({allChats.chats.Count} entries), trying GetAllDialogs…");

        var dialogs = await _client.Messages_GetAllDialogs();
        TL.InputPeer? resolved = null;
        if (dialogs.chats.TryGetValue(chatId, out var dialogChat)) resolved = dialogChat;
        else if (dialogs.users.TryGetValue(chatId, out var user))  resolved = user;

        if (resolved is not null)
        {
            _cachedPeer          = resolved;
            _cachedPeerForChatId = chatId;
            return resolved;
        }

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
        var client = _client;
        _client = null;
        _clientLock.Dispose();

        if (client is null) return;

        // WTelegram.Client.Dispose() sends a graceful disconnect packet and waits for
        // the server ACK — this blocks for 1-3 s on the calling thread. Since we're
        // shutting down we fire-and-forget it on the thread pool; the OS will reclaim
        // all handles when the process exits regardless.
        Task.Run(() =>
        {
            try   { client.Dispose(); }
            catch { /* suppress: process is exiting */ }
        });
    }

    private async Task EnsureClientAsync()
    {
        // Fast path — session is already live, skip locking entirely.
        if (_isReady && _client != null) return;
        if (!IsAuthorized) return;

        await _clientLock.WaitAsync();
        try
        {
            // Re-check inside the lock — another concurrent call may have already
            // initialized the client while we were waiting.
            if (_isReady && _client != null) return;

            _client = new WTelegram.Client(ConfigProvider, new DpapiSessionStream(SessionPath));

            // LoginUserIfNeeded() has no built-in timeout — a hung MTProto handshake
            // would block the calling thread indefinitely and freeze the UI for up to 30s
            // (HomeViewModel.Dispose waits on _pendingStopTask). We impose a hard 30s cap.
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var loginTask  = RunOnDedicatedThreadAsync(() => _client.LoginUserIfNeeded());
            var completed  = await Task.WhenAny(loginTask, Task.Delay(Timeout.Infinite, cts.Token));

            if (completed != loginTask)
            {
                Debug.WriteLine("[TG] ✗ Session restore timed out after 30 s");
                ErrorReporter.Report("TELEGRAM", "Відновлення сесії Telegram перевищило 30 с — перевірте з'єднання.");
                _client.Dispose();
                _client  = null;
                _isReady = false;
                return;
            }

            await loginTask; // propagate any exception from the completed task
            _isReady = true;
            Debug.WriteLine("[TG] Session restored from file");
        }
        catch (TL.RpcException ex) when (ex.Code == 401)
        {
            Debug.WriteLine($"[TG] ✗ Auth key unregistered on session restore — invalidating");
            HandleAuthKeyUnregistered();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TG] ✗ Failed to restore session: {ex.Message}");
            ErrorReporter.Report("TELEGRAM", $"Не вдалося відновити сесію: {ex.Message}", ex);
            _isReady = false;
            _client  = null;
        }
        finally
        {
            _clientLock.Release();
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
            dispatcher.BeginInvoke(() =>
                NotificationService.ShowError("Час очікування введення Telegram вийшов. Спробуйте авторизуватися знову."));
            return null;
        }

        return tcs.Task.Result;
    }

    // Runs the given async work on a single dedicated background thread with its own
    // message-pump SynchronizationContext, instead of the shared ThreadPool.
    // LoginUserIfNeeded() invokes ConfigProvider synchronously mid-flow to request the
    // verification code/2FA password (AskForInput above), which blocks the calling thread
    // for up to 3 minutes. Without this, every continuation of that async chain — including
    // ones resumed after internal network awaits — would be scheduled on a ThreadPool
    // worker, starving the shared pool for the duration of the wait.
    private static Task<T> RunOnDedicatedThreadAsync<T>(Func<Task<T>> asyncFunc)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var pump = new SingleThreadSyncContext();
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                var task = asyncFunc();
                task.ContinueWith(t =>
                {
                    pump.Complete();
                    if (t.IsFaulted)       tcs.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) tcs.TrySetCanceled();
                    else                   tcs.TrySetResult(t.Result);
                }, TaskScheduler.Default);
                pump.RunOnCurrentThread();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "TelegramAuthThread" };
        thread.Start();

        return tcs.Task;
    }

    private sealed class SingleThreadSyncContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Pump already completed (the wrapped async chain finished) but some
                // unrelated continuation — e.g. a background update-listener the client
                // spun up as a side effect of logging in — still captured this context
                // and is trying to post to it. Fall back to the ThreadPool instead of
                // throwing on whatever thread is posting.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunOnCurrentThread()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        public void Complete() => _queue.CompleteAdding();
    }
}
