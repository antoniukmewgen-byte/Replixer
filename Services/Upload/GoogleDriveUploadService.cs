using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Replixer.Services.Upload;

public class GoogleDriveUploadService
{
    private const string ServiceAccountResourceName = "Replixer.service_account.json";

    private readonly DriveService? _service;

    public bool IsAuthorized => _service is not null;

    public event Action<int>?    ProgressChanged;  // 0–100
    public event Action<string>? UploadCompleted;  // web view link
    public event Action<string>? UploadFailed;     // error message

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

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Checks access to the folder. Returns null on success, or an error message on failure.</summary>
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

    /// <summary>Uploads <paramref name="filePath"/> to <paramref name="folderId"/> on Google Drive.
    /// Returns the webViewLink on success, or null on failure.</summary>
    public async Task<string?> UploadAsync(string filePath, string? folderId, CancellationToken ct = default)
    {
        Debug.WriteLine($"[GDrive] ── UploadAsync ─────────────────────────────");
        Debug.WriteLine($"[GDrive] File     : {filePath}");
        Debug.WriteLine($"[GDrive] FolderId : {(folderId ?? "(root)")}");

        if (_service is null)
        {
            const string err = "Service account not initialized. Check that service_account.json is embedded.";
            Debug.WriteLine($"[GDrive] ✗ {err}");
            UploadFailed?.Invoke(err);
            return null;
        }

        try
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name     = Path.GetFileName(filePath),
                MimeType = "audio/mpeg",
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

            var request = _service.Files.Create(metadata, stream, "audio/mpeg");
            request.Fields           = "id,webViewLink,name";
            request.SupportsAllDrives = true;

            int lastReported = 0;
            request.ProgressChanged += p =>
            {
                if (p.Status == UploadStatus.Uploading && totalBytes > 0)
                {
                    int pct = (int)(p.BytesSent * 100 / totalBytes);
                    ProgressChanged?.Invoke(pct);

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
                string id   = request.ResponseBody?.Id          ?? "(no id)";
                string name = request.ResponseBody?.Name        ?? "(no name)";
                string link = request.ResponseBody?.WebViewLink ?? string.Empty;
                Debug.WriteLine($"[GDrive] ✓ File id   : {id}");
                Debug.WriteLine($"[GDrive] ✓ File name : {name}");
                Debug.WriteLine($"[GDrive] ✓ View link : {link}");
                UploadCompleted?.Invoke(link);
                return link;
            }

            string error = result.Exception is not null
                ? $"{result.Exception.GetType().Name}: {result.Exception.Message}"
                : "Unknown error";
            Debug.WriteLine($"[GDrive] ✗ Upload failed: {error}");
            UploadFailed?.Invoke(error);
            return null;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[GDrive] Upload cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDrive] ✗ Exception: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[GDrive]   StackTrace: {ex.StackTrace}");
            UploadFailed?.Invoke(ex.Message);
            return null;
        }
        finally
        {
            Debug.WriteLine("[GDrive] ───────────────────────────────────────────");
        }
    }

    /// <summary>Finds or creates a subfolder with <paramref name="userName"/> inside <paramref name="parentFolderId"/>.
    /// Returns the folder ID, or null on failure.</summary>
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
            return null;
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static DriveService CreateService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ServiceAccountResourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{ServiceAccountResourceName}' not found. " +
                "Add service_account.json to the project as EmbeddedResource.");

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
