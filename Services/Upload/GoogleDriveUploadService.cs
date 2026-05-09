using EchoVault.Infrastructure;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace EchoVault.Services.Upload;

public class GoogleDriveUploadService
{
    private static readonly string TokenStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EchoVault", "google_token");

    private static readonly string[] Scopes = { DriveService.Scope.Drive };

    // Embedded resource name: <DefaultNamespace>.<FileName>
    private const string CredentialsResourceName = "EchoVault.credentials.json";

    public bool IsAuthorized => Directory.Exists(TokenStorePath) &&
                                Directory.GetFiles(TokenStorePath).Length > 0;

    public event Action<int>?    ProgressChanged;  // 0–100
    public event Action<string>? UploadCompleted;  // web view link
    public event Action<string>? UploadFailed;     // error message

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Opens browser for OAuth consent (first time) or uses cached token.</summary>
    public async Task<bool> AuthorizeAsync(CancellationToken ct = default)
    {
        try
        {
            var credential = await GetCredentialAsync(ct);
            Debug.WriteLine($"[GDrive] Authorized — token type: {credential.Token.TokenType}, stale: {credential.Token.IsStale}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GDrive] Auth failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Uploads <paramref name="filePath"/> to <paramref name="folderId"/> on Google Drive.
    /// Returns the webViewLink on success, or null on failure.</summary>
    public async Task<string?> UploadAsync(string filePath, string? folderId, CancellationToken ct = default)
    {
        Debug.WriteLine($"[GDrive] ── UploadAsync ─────────────────────────────");
        Debug.WriteLine($"[GDrive] File     : {filePath}");
        Debug.WriteLine($"[GDrive] FolderId : {(folderId ?? "(root)")}");

        try
        {
            Debug.WriteLine("[GDrive] Getting credential …");
            var credential = await GetCredentialAsync(ct);
            Debug.WriteLine($"[GDrive] Credential OK — access token present: {!string.IsNullOrEmpty(credential.Token.AccessToken)}");

            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName       = "EchoVault",
            });

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

            var request = service.Files.Create(metadata, stream, "audio/mpeg");
            request.Fields = "id,webViewLink,name";

            int lastReported = 0;
            request.ProgressChanged += p =>
            {
                if (p.Status == UploadStatus.Uploading && totalBytes > 0)
                {
                    int pct = (int)(p.BytesSent * 100 / totalBytes);
                    ProgressChanged?.Invoke(pct);

                    // Log every 25 %
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

            Debug.WriteLine($"[GDrive] Starting upload …");
            var result = await request.UploadAsync(ct);
            Debug.WriteLine($"[GDrive] Upload finished — status: {result.Status}");

            if (result.Status == UploadStatus.Completed)
            {
                string id   = request.ResponseBody?.Id           ?? "(no id)";
                string name = request.ResponseBody?.Name         ?? "(no name)";
                string link = request.ResponseBody?.WebViewLink  ?? string.Empty;
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
            if (result.Exception is not null)
                Debug.WriteLine($"[GDrive]   StackTrace: {result.Exception.StackTrace}");
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
            Debug.WriteLine($"[GDrive] ───────────────────────────────────────────");
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static async Task<UserCredential> GetCredentialAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(CredentialsResourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{CredentialsResourceName}' not found. " +
                "Make sure credentials.json is included as EmbeddedResource in the project.");

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            "user",
            ct,
            new FileDataStore(TokenStorePath, true),
            new ChromeCodeReceiver());
    }
}
