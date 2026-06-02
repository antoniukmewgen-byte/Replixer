using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Replixer.Services;

public sealed record UpdateFileEntry(
    [property: JsonPropertyName("path")]   string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("url")]    string Url);

public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("files")]   IReadOnlyList<UpdateFileEntry> Files);

public sealed record UpdateInfo(Version NewVersion, UpdateManifest Manifest);

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
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tagName.TrimStart('v'), out var remoteVersion))
                return null;

            if (remoteVersion <= GetCurrentVersion())
                return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var manifestUrl  = asset.GetProperty("browser_download_url").GetString() ?? "";
                var manifestJson = await _http.GetStringAsync(manifestUrl, ct);
                var manifest     = JsonSerializer.Deserialize<UpdateManifest>(manifestJson);
                if (manifest is null) return null;

                return new UpdateInfo(remoteVersion, manifest);
            }
        }
        catch { }

        return null;
    }

    public async Task<string> DownloadUpdatesAsync(
        UpdateInfo         info,
        IProgress<double>? progress = null,
        CancellationToken  ct       = default)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var stagingDir = Path.Combine(Path.GetTempPath(), $"Replixer_update_{info.NewVersion}");
        Directory.CreateDirectory(stagingDir);

        var toDownload = info.Manifest.Files
            .Where(f => NeedsUpdate(Path.Combine(installDir, f.Path), f.Sha256))
            .ToList();

        for (var i = 0; i < toDownload.Count; i++)
        {
            var entry    = toDownload[i];
            var destPath = Path.Combine(stagingDir, entry.Path);

            var dir = Path.GetDirectoryName(destPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var fileIndex    = i;
            var fileProgress = progress is not null
                ? new Progress<double>(p => progress.Report((fileIndex + p) / toDownload.Count))
                : null;

            await DownloadFileAsync(entry.Url, destPath, fileProgress, ct);
        }

        progress?.Report(1.0);
        return stagingDir;
    }

    public void LaunchUpdaterAndExit(string stagingDir, Version newVersion)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var mainExe    = Path.Combine(installDir, "Replixer.exe");
        var pid        = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), "replixer_update.ps1");

        var script = $$"""
            $proc = Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
            if ($proc) { $proc.WaitForExit(30000) }

            Get-ChildItem -LiteralPath '{{Esc(stagingDir)}}' -File -Recurse | ForEach-Object {
                $rel     = $_.FullName.Substring('{{Esc(stagingDir)}}'.Length).TrimStart('\')
                $dest    = Join-Path '{{Esc(installDir)}}' $rel
                $destDir = Split-Path $dest -Parent
                if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
            }

            Remove-Item -LiteralPath '{{Esc(stagingDir)}}' -Recurse -Force -ErrorAction SilentlyContinue

            $regId  = '{A3F2B1C4-7E8D-4F5A-9C3B-2D6E1F0A8B7C}_is1'
            $newVer = '{{newVersion.ToString(3)}}'
            foreach ($hive in @('HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                                 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
                $key = Join-Path $hive $regId
                if (Test-Path $key) {
                    Set-ItemProperty -LiteralPath $key -Name 'DisplayVersion' -Value $newVer -ErrorAction SilentlyContinue
                }
            }

            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue

            Start-Process -FilePath '{{Esc(mainExe)}}'
            """;

        File.WriteAllText(scriptPath, script, System.Text.Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
        });

        System.Windows.Application.Current.Dispatcher.Invoke(
            System.Windows.Application.Current.Shutdown);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool NeedsUpdate(string localPath, string expectedSha256)
    {
        if (!File.Exists(localPath)) return true;
        return !ComputeSha256(localPath).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var sha    = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private async Task DownloadFileAsync(
        string             url,
        string             destPath,
        IProgress<double>? progress,
        CancellationToken  ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var  total      = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);

        var  buffer     = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0) progress?.Report((double)downloaded / total);
        }
    }

    // Escape single-quotes for PowerShell single-quoted strings.
    private static string Esc(string path) => path.Replace("'", "''");
}
