using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private const string Owner      = "antoniukmewgen-byte";
    private const string Repo       = "Replixer";
    private const string Branch     = "main";
    private const string VersionUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/version.json";

    private static readonly HttpClient _http = new();

    static UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Replixer", GetCurrentVersion().ToString(3)));
    }

    public static Version GetCurrentVersion()
        => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static readonly TimeSpan NetworkSettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryCooldown       = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Startup variant of <see cref="CheckForUpdateAsync"/>: if the first attempt fails
    /// because there's genuinely no network yet (right after boot/login while Wi-Fi/VPN is
    /// still connecting, or after waking from sleep), keeps listening for Windows to report
    /// connectivity and retries — for as long as the app runs, not just a few minutes. There's
    /// no artificial deadline; the only thing that stops this is <paramref name="ct"/> being
    /// cancelled, which <see cref="Replixer.ViewModels.MainViewModel"/> does on app shutdown.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateWithNetworkWaitAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await CheckForUpdateAsync(ct);
            if (info is not null || NetworkInterface.GetIsNetworkAvailable())
                return info;

            while (true)
            {
                if (!await WaitForNetworkAvailableAsync(ct))
                    return null; // ct cancelled — app is shutting down

                await Task.Delay(NetworkSettleDelay, ct); // DNS/adapter needs a moment after "available"
                info = await CheckForUpdateAsync(ct);
                if (info is not null || NetworkInterface.GetIsNetworkAvailable())
                    return info;

                // Adapter reports "up" but the actual check still failed (e.g. captive
                // portal, DNS server itself still down) — avoid hammering on flapping
                // connections before listening for the next availability event.
                await Task.Delay(RetryCooldown, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForNetworkAvailableAsync(CancellationToken ct)
    {
        if (NetworkInterface.GetIsNetworkAvailable()) return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkAvailabilityChangedEventHandler handler = (_, e) =>
        {
            if (e.IsAvailable) tcs.TrySetResult(true);
        };

        NetworkChange.NetworkAvailabilityChanged += handler;
        using var reg = ct.Register(() => tcs.TrySetResult(false));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= handler;
        }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json     = await _http.GetStringAsync(VersionUrl, ct);
                var manifest = JsonSerializer.Deserialize<UpdateManifest>(json);
                if (manifest is null) return null;

                if (!Version.TryParse(manifest.Version.TrimStart('v'), out var remoteVersion))
                    return null;

                if (remoteVersion <= GetCurrentVersion())
                    return null;

                return new UpdateInfo(remoteVersion, manifest);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (attempt < RetryCount && IsTransient(ex))
            {
                var delay = RetryDelays[attempt];
                Debug.WriteLine($"[Update] Check attempt {attempt + 1} failed ({ex.Message}). Retrying in {delay.TotalSeconds}s…");
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // Retries exhausted but it's still a plain connectivity failure (DNS not
                // resolving, host unreachable, etc.) — typical right after boot/login when
                // network isn't up yet, or a temporary ISP/VPN blip. Not a code bug and it
                // self-heals on the next check, so don't spam the error channel for it.
                Debug.WriteLine($"[Update] Check failed after {RetryCount} retries (still transient): {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("UpdateService", "Не вдалося перевірити оновлення", ex);
                return null;
            }
        }
    }

    /// <returns>
    /// Path to the staging directory, or <see langword="null"/> when every local file
    /// already matches the expected SHA-256 and nothing needs to be copied.
    /// </returns>
    public async Task<string?> DownloadUpdatesAsync(
        UpdateInfo         info,
        IProgress<double>? progress = null,
        CancellationToken  ct       = default)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

        var toDownload = info.Manifest.Files
            .Where(f => NeedsUpdate(Path.Combine(installDir, f.Path), f.Sha256))
            .ToList();

        if (toDownload.Count == 0)
        {
            progress?.Report(1.0);
            return null;
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), $"Replixer_update_{info.NewVersion}");
        Directory.CreateDirectory(stagingDir);

        try
        {
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
        }
        catch
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            throw;
        }

        progress?.Report(1.0);
        return stagingDir;
    }

    /// <param name="stagingDir">
    /// Directory with staged files, or <see langword="null"/> when all files were
    /// already up-to-date (only a restart is needed, no copy step).
    /// </param>
    public void LaunchUpdaterAndExit(string? stagingDir, Version newVersion)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var mainExe    = Path.Combine(installDir, "Replixer.exe");
        var pid        = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), "replixer_update.ps1");
        var logPath    = Path.Combine(Path.GetTempPath(), "replixer_update.log");

        string script;

        if (stagingDir is null)
        {
            // Nothing to copy — just restart.
            script = $$"""
                $proc = Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
                if ($proc) { $proc.WaitForExit(30000) }
                Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath '{{Esc(mainExe)}}'
                """;
        }
        else
        {
            script = $$"""
                $ErrorActionPreference = 'Stop'
                $logPath = '{{Esc(logPath)}}'

                # Killing the old process (below) doesn't guarantee the OS has released its
                # module handle on Replixer.dll/.exe the instant WaitForExit returns — AV/EDR
                # real-time scanning or a slow disk can hold it a little longer. Retry instead
                # of failing the whole update on one transient "used by another process".
                function Copy-ItemWithRetry {
                    param([string]$Path, [string]$Destination, [int]$MaxAttempts = 10, [int]$DelayMs = 300)
                    for ($i = 1; $i -le $MaxAttempts; $i++) {
                        try { Copy-Item -LiteralPath $Path -Destination $Destination -Force; return }
                        catch {
                            if ($i -eq $MaxAttempts) { throw }
                            Start-Sleep -Milliseconds $DelayMs
                        }
                    }
                }

                try {
                    $proc = Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
                    if ($proc) {
                        $exited = $proc.WaitForExit(30000)
                        if (-not $exited) { $proc.Kill() }
                        $proc.WaitForExit(5000)
                    }
                    Start-Sleep -Milliseconds 500

                    # Remove obsolete files that are no longer shipped
                    @('FlaUI.UIA3.dll', 'FlaUI.Core.dll', 'Interop.UIAutomationClient.dll') | ForEach-Object {
                        $obsolete = Join-Path '{{Esc(installDir)}}' $_
                        if (Test-Path $obsolete) { Remove-Item -LiteralPath $obsolete -Force -ErrorAction SilentlyContinue }
                    }

                    Get-ChildItem -LiteralPath '{{Esc(stagingDir)}}' -File -Recurse | ForEach-Object {
                        $rel     = $_.FullName.Substring('{{Esc(stagingDir)}}'.Length).TrimStart('\')
                        $dest    = Join-Path '{{Esc(installDir)}}' $rel
                        $destDir = Split-Path $dest -Parent
                        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
                        Copy-ItemWithRetry -Path $_.FullName -Destination $dest
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
                }
                catch {
                    "[$(Get-Date -Format 'u')] Update failed: $_" | Out-File -FilePath $logPath -Encoding utf8 -Append
                }
                finally {
                    Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
                    Start-Process -FilePath '{{Esc(mainExe)}}'
                }
                """;
        }

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

    // Transient errors that are safe to retry (network blip, VPN reconnect, etc.).
    // OperationCanceledException and non-retriable HTTP errors bubble straight out.
    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException { StatusCode: null or
            System.Net.HttpStatusCode.RequestTimeout or
            System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout } or
        IOException or
        SocketException;

    private const int RetryCount = 3;
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    private async Task DownloadFileAsync(
        string             url,
        string             destPath,
        IProgress<double>? progress,
        CancellationToken  ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0) progress?.Report(0);

            try
            {
                await DownloadFileOnceAsync(url, destPath, progress, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryCount && IsTransient(ex))
            {
                var delay = RetryDelays[attempt];
                Debug.WriteLine($"[Update] Attempt {attempt + 1} failed ({ex.Message}). Retrying in {delay.TotalSeconds}s…");
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task DownloadFileOnceAsync(
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
