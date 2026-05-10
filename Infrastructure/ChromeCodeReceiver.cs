using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Replixer.Infrastructure;

/// <summary>
/// OAuth code receiver that forces the authorization URL to open in Google Chrome
/// instead of the system default browser.
/// </summary>
public class ChromeCodeReceiver : ICodeReceiver
{
    private const string CallbackPath = "/authorize/";

    private readonly int _port = GetFreePort();

    public string RedirectUri => $"http://localhost:{_port}{CallbackPath}";

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url,
        CancellationToken taskCancellationToken)
    {
        string authUrl = url.Build().AbsoluteUri;

        OpenInChrome(authUrl);

        var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        try
        {
            // Chrome may fire several requests (favicon, prefetch…).
            // Loop until we get the one that actually carries the OAuth code/error.
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(taskCancellationToken);

                var qs = context.Request.QueryString;

                // Read the code BEFORE touching the response (response.Close can
                // invalidate the request object on some runtimes).
                string? code  = qs["code"];
                string? error = qs["error"];

                // Respond regardless — suppres any I/O errors on secondary requests
                try { await RespondAndCloseAsync(context.Response, taskCancellationToken); }
                catch { /* ignore — secondary browser requests */ }

                // Only return when we have the real OAuth callback
                if (code is not null || error is not null)
                    return new AuthorizationCodeResponseUrl { Code = code, Error = error };
            }
        }
        finally
        {
            // Stop + Close so background threads don't access a disposed listener
            listener.Stop();
            listener.Close();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void OpenInChrome(string url)
    {
        string? chrome = FindChromePath();

        if (chrome is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = chrome,
                Arguments       = $"--new-window \"{url}\"",
                UseShellExecute = false,
            });
        }
        else
        {
            // Chrome not found — fall back to default browser
            Debug.WriteLine("[ChromeCodeReceiver] Chrome not found, using default browser.");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private static string? FindChromePath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        ];

        return Array.Find(candidates, File.Exists);
    }

    private static async Task RespondAndCloseAsync(HttpListenerResponse response, CancellationToken ct)
    {
        const string html =
            "<html><head><meta charset='utf-8'></head><body style='font-family:sans-serif;text-align:center;margin-top:80px'>" +
            "<h2>✅ Авторизацію завершено</h2>" +
            "<p>Можна закрити цю вкладку та повернутися до Replixer.</p>" +
            "</body></html>";

        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        response.ContentType     = "text/html; charset=utf-8";

        await response.OutputStream.WriteAsync(buffer, ct);
        response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
