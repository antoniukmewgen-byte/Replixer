using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Replixer.Services;

public sealed record UpdateInfo(Version NewVersion, string DownloadUrl);

public sealed class UpdateService
{
    private const string Owner  = "antoniukmewgen-byte";
    private const string Repo   = "Replixer";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient _http = new();

    static UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Replixer", GetCurrentVersion().ToString(3)));
    }

    public static Version GetCurrentVersion()
        => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var remoteVersion))
                return null;

            if (remoteVersion <= GetCurrentVersion())
                return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.Equals("Replixer-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                return new UpdateInfo(remoteVersion, url);
            }
        }
        catch { }

        return null;
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "Replixer-Setup.exe");

        using var response = await _http.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(tempPath);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
                progress?.Report((double)downloaded / total);
        }

        return tempPath;
    }

    public void LaunchInstallerAndExit(string installerPath)
    {
        // /SILENT    — без діалогів інсталера
        // /RESTARTEXITCODE=0 — повідомляє Inno Setup що після встановлення
        //                      треба перезапустити програму
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments       = "/SILENT /RESTARTEXITCODE=0"
        });

        System.Windows.Application.Current.Shutdown();
    }
}
