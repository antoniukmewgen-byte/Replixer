using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Replixer.Infrastructure;
using Replixer.Services;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Replixer.Services.Upload;

public class GoogleDriveUploadService
{
    // Сервіс-акаунт спільний для ВСІХ завантажень (живий дзвінок, фоновий retry записів,
    // фоновий retry скріншотів, недодзвони) — тож і ліміт запитів (userRateLimitExceeded/
    // rateLimitExceeded, 403/429) теж спільний. Раніше кожен виклик UploadAsync, зіткнувшись
    // із таким лімітом, одразу здавався (GoogleApiException не потрапляв під isNetworkError),
    // а зовнішні PendingUploadRetryService/PendingScreenshotUploadRetryService тикають кожні
    // 10с НЕЗАЛЕЖНО від причини попередньої помилки — тобто щоразу знову била в API, не
    // даючи вікну обмеження в Google взагалі спливти (реальний випадок: одна й та сама
    // помилка трималась ~50 хв поспіль). Цей cooldown — спільний "запобіжник" на рівні
    // сервісу: щойно ловимо ліміт, наступні ~100с (вікно, яке сама Google згадує в описі
    // userRateLimitExceeded) НІХТО не робить реального мережевого запиту — UploadAsync
    // одразу повертає null, і виклик просто чекає на наступний тик фонового сервісу.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(100);
    private DateTime _rateLimitedUntilUtc = DateTime.MinValue;

    private readonly DriveService? _service;

    public bool IsAuthorized => _service is not null;

    public GoogleDriveUploadService()
    {
        try
        {
            _service = CreateService();
            Debug.WriteLine("[GDrive] Service account initialized successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDrive] Failed to initialize service account: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<string?> TestFolderAccessAsync(string folderId, CancellationToken ct = default)
    {
        Debug.WriteLine($"[GDrive] ── TestFolderAccess ──────────────────────────");
        Debug.WriteLine($"[GDrive] Service initialized : {_service is not null}");
        Debug.WriteLine($"[GDrive] FolderId            : '{folderId}'");

        if (_service is null)
        {
            const string err = "service_account.json не знайдено або має невірний формат";
            Debug.WriteLine($"[GDrive] ✗ {err}");
            return err;
        }

        if (string.IsNullOrWhiteSpace(folderId))
        {
            const string err = "ID папки не вказано";
            Debug.WriteLine($"[GDrive] ✗ {err}");
            return err;
        }

        try
        {
            var req = _service.Files.Get(folderId);
            req.Fields           = "id,name,mimeType";
            req.SupportsAllDrives = true;
            var file = await req.ExecuteAsync(ct);
            Debug.WriteLine($"[GDrive] ✓ Folder found: '{file.Name}' ({file.MimeType})");
            return null;
        }
        catch (Google.GoogleApiException apiEx)
        {
            string err = $"[{(int)apiEx.HttpStatusCode}] {apiEx.Error?.Message ?? apiEx.Message}";
            Debug.WriteLine($"[GDrive] ✗ API error: {err}");
            if (apiEx.Error?.Errors != null)
                foreach (var e in apiEx.Error.Errors)
                    Debug.WriteLine($"[GDrive]   Detail: {e.Reason} — {e.Message}");
            return err;
        }
        catch (Exception ex)
        {
            string err = $"{ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[GDrive] ✗ Exception: {err}");
            return err;
        }
        finally
        {
            Debug.WriteLine($"[GDrive] ───────────────────────────────────────────");
        }
    }

    // mimeType — за замовчуванням "audio/mpeg" для зворотної сумісності з існуючими викликами
    // (завантаження аудіозаписів дзвінків); для скріншотів викликач передає "image/png".
    public async Task<string?> UploadAsync(string filePath, string? folderId, string mimeType = "audio/mpeg", CancellationToken ct = default)
    {
        if (_service is null)
        {
            const string err = "Service account not initialized. Check that service_account.json is embedded.";
            Debug.WriteLine($"[GDrive] ✗ {err}");
            return null;
        }

        var cooldownLeft = _rateLimitedUntilUtc - DateTime.UtcNow;
        if (cooldownLeft > TimeSpan.Zero)
        {
            Debug.WriteLine($"[GDrive] Skipping — still in rate-limit cooldown for {cooldownLeft.TotalSeconds:F0}s more");
            return null;
        }

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (link, isNetworkError, ex) = await TryUploadOnceAsync(filePath, folderId, mimeType, attempt, ct);
            if (link is not null) return link;

            if (IsRateLimitError(ex))
            {
                _rateLimitedUntilUtc = DateTime.UtcNow + RateLimitCooldown;
                ErrorReporter.Report("GOOGLE_DRIVE",
                    $"Google Drive: перевищено ліміт запитів (userRateLimitExceeded) — призупиняємо завантаження на {RateLimitCooldown.TotalSeconds:F0}с, щоб не заганяти ліміт у нескінченний цикл.");
                return null;
            }

            if (!isNetworkError) return null;
            if (attempt == maxAttempts) break;
            var delay = TimeSpan.FromSeconds(5 * Math.Pow(2, attempt - 1)); // 5s, 10s
            Debug.WriteLine($"[GDrive] Network error, retrying in {delay.TotalSeconds:F0}s (attempt {attempt}/{maxAttempts})…");
            await Task.Delay(delay, ct);
        }

        ErrorReporter.Report("GOOGLE_DRIVE", "Немає з'єднання з Google Drive після 3 спроб — перевірте інтернет.");
        return null;
    }

    // userRateLimitExceeded/rateLimitExceeded/quotaExceeded — усі приходять як GoogleApiException
    // з HTTP 403 (інколи 429), з причиною в Error.Errors[].Reason. На відміну від звичайної
    // мережевої помилки, повторювати такий запит одразу — контрпродуктивно: саме часті повтори
    // й тримають вікно обмеження відкритим.
    private static bool IsRateLimitError(Exception? ex) =>
        ex is Google.GoogleApiException apiEx &&
        (apiEx.HttpStatusCode == System.Net.HttpStatusCode.Forbidden ||
         apiEx.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests) &&
        (apiEx.Error?.Errors?.Any(e => e.Reason is "userRateLimitExceeded" or "rateLimitExceeded" or "quotaExceeded") ?? false);

    private async Task<(string? link, bool isNetworkError, Exception? ex)> TryUploadOnceAsync(
        string filePath, string? folderId, string mimeType, int attempt, CancellationToken ct)
    {
        Debug.WriteLine($"[GDrive] ── UploadAsync (attempt {attempt}) ──────────────────");
        Debug.WriteLine($"[GDrive] File     : {filePath}");
        Debug.WriteLine($"[GDrive] FolderId : {(folderId ?? "(root)")}");

        try
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name     = Path.GetFileName(filePath),
                MimeType = mimeType,
            };

            if (!string.IsNullOrWhiteSpace(folderId))
            {
                metadata.Parents = new List<string> { folderId };
                Debug.WriteLine($"[GDrive] Target folder : {folderId}");
            }
            else
            {
                Debug.WriteLine("[GDrive] Target folder : Drive root");
            }

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            long totalBytes = stream.Length;
            Debug.WriteLine($"[GDrive] File size : {totalBytes:N0} bytes ({totalBytes / 1024.0 / 1024.0:F2} MB)");

            var request = _service!.Files.Create(metadata, stream, mimeType);
            request.Fields            = "id,webContentLink,name";
            request.SupportsAllDrives = true;

            int lastReported = 0;
            request.ProgressChanged += p =>
            {
                if (p.Status == UploadStatus.Uploading && totalBytes > 0)
                {
                    int pct = (int)(p.BytesSent * 100 / totalBytes);
                    if (pct / 25 > lastReported / 25)
                    {
                        lastReported = pct;
                        Debug.WriteLine($"[GDrive] Progress : {pct}% ({p.BytesSent:N0} / {totalBytes:N0} bytes)");
                    }
                }
                else if (p.Status != UploadStatus.Uploading)
                {
                    Debug.WriteLine($"[GDrive] Status : {p.Status}  exception: {p.Exception?.Message ?? "none"}");
                }
            };

            Debug.WriteLine("[GDrive] Starting upload …");
            var result = await request.UploadAsync(ct);
            Debug.WriteLine($"[GDrive] Upload finished — status: {result.Status}");

            if (result.Status == UploadStatus.Completed)
            {
                string id   = request.ResponseBody?.Id             ?? "(no id)";
                string name = request.ResponseBody?.Name           ?? "(no name)";
                string link = request.ResponseBody?.WebContentLink ?? string.Empty;
                Debug.WriteLine($"[GDrive] ✓ File id      : {id}");
                Debug.WriteLine($"[GDrive] ✓ File name    : {name}");
                Debug.WriteLine($"[GDrive] ✓ Content link : {link}");
                return (link, false, null);
            }

            string error = result.Exception is not null
                ? $"{result.Exception.GetType().Name}: {result.Exception.Message}"
                : "Unknown error";
            Debug.WriteLine($"[GDrive] ✗ Upload failed: {error}");

            bool isNet = result.Exception is System.Net.Http.HttpRequestException
                                          or System.Net.Sockets.SocketException;
            // Rate-limit звітуємо окремо й коротко на рівні UploadAsync (там же виставляється
            // cooldown) — тут не дублюємо тим самим сирим стектрейсом.
            if (!isNet && !IsRateLimitError(result.Exception))
                ErrorReporter.Report("GOOGLE_DRIVE", $"Upload failed: {error}", result.Exception);
            return (null, isNet, result.Exception);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[GDrive] Upload cancelled");
            return (null, false, null);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Debug.WriteLine($"[GDrive] ✗ Network error: {ex.Message}");
            return (null, true, ex);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Debug.WriteLine($"[GDrive] ✗ Socket error: {ex.Message}");
            return (null, true, ex);
        }
        catch (FileNotFoundException ex)
        {
            // Файл, який мав вантажитись, фізично зник з диска між тим, як фоновий retry
            // вирішив, що він ще є (File.Exists у RetryEntryAsync), і фактичним відкриттям
            // потоку тут — найчастіше це антивірус/EDR, що прибрав щойно створений
            // аудіо/скрін-файл із Temp, або хтось вручну чистив Temp-папку. У самому
            // застосунку немає жодного шляху, який видаляв би файл до успішного завершення
            // ВСІХ кроків (Drive+Telegram+Kommo) — тож ретраїти тут нема сенсу, наступний
            // тик фонового сервісу й так побачить, що файлу немає, і сам зніме прапорці.
            Debug.WriteLine($"[GDrive] ✗ File vanished before upload: {ex.Message}");
            ErrorReporter.Report("GOOGLE_DRIVE",
                $"Файл зник з диска до завантаження (ймовірно, антивірус або очищення Temp-папки): {Path.GetFileName(filePath)}");
            return (null, false, ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDrive] ✗ Exception: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[GDrive]   StackTrace: {ex.StackTrace}");
            if (!IsRateLimitError(ex))
                ErrorReporter.Report("GOOGLE_DRIVE", $"Upload exception: {ex.Message}", ex);
            return (null, false, ex);
        }
        finally
        {
            Debug.WriteLine("[GDrive] ───────────────────────────────────────────");
        }
    }

    public async Task<string?> GetOrCreateUserFolderAsync(string parentFolderId, string userName, CancellationToken ct = default)
    {
        if (_service is null || string.IsNullOrWhiteSpace(parentFolderId) || string.IsNullOrWhiteSpace(userName))
            return null;

        try
        {
            var listReq = _service.Files.List();
            listReq.Q                        = $"name='{EscapeQuery(userName)}' and mimeType='application/vnd.google-apps.folder' and '{parentFolderId}' in parents and trashed=false";
            listReq.Fields                   = "files(id,name)";
            listReq.SupportsAllDrives        = true;
            listReq.IncludeItemsFromAllDrives = true;
            var listResult = await listReq.ExecuteAsync(ct);

            if (listResult.Files?.Count > 0)
            {
                Debug.WriteLine($"[GDrive] User folder found: {listResult.Files[0].Id}");
                return listResult.Files[0].Id;
            }

            var folder = new Google.Apis.Drive.v3.Data.File
            {
                Name     = userName,
                MimeType = "application/vnd.google-apps.folder",
                Parents  = new List<string> { parentFolderId },
            };

            var createReq = _service.Files.Create(folder);
            createReq.Fields           = "id";
            createReq.SupportsAllDrives = true;
            var created = await createReq.ExecuteAsync(ct);
            Debug.WriteLine($"[GDrive] User folder created: {created.Id}");
            return created.Id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDrive] GetOrCreateUserFolder failed: {ex.Message}");
            ErrorReporter.Report("GOOGLE_DRIVE", $"Не вдалося створити/знайти папку користувача \"{userName}\": {ex.Message}", ex);
            return null;
        }
    }

    private static DriveService CreateService()
    {
        var json = AppSecrets.GoogleServiceAccountJson;
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("GoogleServiceAccountJson is not configured in AppSecrets.");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var credential = GoogleCredential.FromStream(stream)
            .CreateScoped(DriveService.Scope.Drive);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "Replixer",
        });
    }

    private static string EscapeQuery(string value) => value.Replace("'", "\\'");
}
