using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Replixer.Services.Upload;

public class KommoService : IDisposable
{
    private readonly AppSettings _settings;

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private const long FirstContactFieldId    = 1225821;
    private const long ProcessingSpeedFieldId = 1225823;
    private const long CallTypeFieldId        = 1226157;

    public KommoService(AppSettings settings) => _settings = settings;

    public bool IsEnabled =>
        _settings.IsKommoEnabled &&
        !string.IsNullOrWhiteSpace(_settings.KommoApiToken) &&
        !string.IsNullOrWhiteSpace(_settings.KommoSubdomain);

    public async Task<string?> TestConnectionAsync(string subdomain, string token)
    {
        if (string.IsNullOrWhiteSpace(subdomain)) return "Субдомен не вказано";
        if (string.IsNullOrWhiteSpace(token))     return "Токен не вказано";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://{subdomain}.kommo.com/api/v4/account");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            Debug.WriteLine($"[Kommo] TestConnection → {(int)res.StatusCode}  {body[..Math.Min(200, body.Length)]}");
            return res.IsSuccessStatusCode ? null : $"Помилка {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<long?> ProcessLeadAsync(string leadUrl, string noteText, DateTime? callStartTime = null, string? callType = null)
    {
        if (!IsEnabled) return null;

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;

        if (leadId is null || string.IsNullOrEmpty(subdomain))
        {
            Debug.WriteLine($"[Kommo] Cannot parse lead URL: {leadUrl}");
            ErrorReporter.Report("KOMMO", $"Не вдалося розпарсити URL ліда: {leadUrl}");
            return null;
        }

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        var noteTask     = PostNoteAsync(baseUrl, token, leadId, noteText);
        var dateTask     = callStartTime.HasValue
            ? TrySetFirstContactDateAsync(baseUrl, token, leadId, callStartTime.Value)
            : Task.CompletedTask;
        var callTypeTask = !string.IsNullOrWhiteSpace(callType)
            ? PatchLeadFieldAsync(baseUrl, token, leadId, CallTypeFieldId, (object)callType)
            : Task.CompletedTask;

        await Task.WhenAll(noteTask, dateTask, callTypeTask);
        return await noteTask;
    }

    public async Task<string?> EditNoteAsync(string leadUrl, long noteId, string noteText, string? callType = null)
    {
        if (!IsEnabled) return "Kommo: інтеграція вимкнена";

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;

        if (leadId is null || string.IsNullOrEmpty(subdomain))
        {
            Debug.WriteLine($"[Kommo] Cannot parse lead URL: {leadUrl}");
            return "Kommo: не вдалося розпарсити URL ліда";
        }

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        try
        {
            var payload = JsonSerializer.Serialize(new[]
            {
                new { id = noteId, note_type = "common", @params = new { text = noteText } }
            });

            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/leads/{leadId}/notes")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            Debug.WriteLine($"[Kommo] Note PATCH → {(int)res.StatusCode}  {body[..Math.Min(500, body.Length)]}");

            if (!res.IsSuccessStatusCode)
                return $"Kommo: помилка {(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] EditNote failed: {ex.Message}");
            return $"Kommo: {ex.Message}";
        }

        if (!string.IsNullOrWhiteSpace(callType))
            await PatchLeadFieldAsync(baseUrl, token, leadId, CallTypeFieldId, (object)callType);

        return null;
    }

    private async Task<long?> PostNoteAsync(string baseUrl, string token, string leadId, string text)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new[]
                {
                    new { entity_id = long.Parse(leadId), note_type = "common", @params = new { text } }
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/leads/{leadId}/notes")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Kommo] Note POST → {(int)res.StatusCode} (attempt {attempt})");

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"PostNote HTTP {(int)res.StatusCode} — lead {leadId}: {snippet}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("_embedded", out var emb) &&
                    emb.TryGetProperty("notes", out var notes) &&
                    notes.GetArrayLength() > 0 &&
                    notes[0].TryGetProperty("id", out var idProp))
                    return idProp.GetInt64();

                return null;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] PostNote attempt {attempt} failed: {ex.Message} — retrying in 2s");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] PostNote failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"PostNote exception — lead {leadId}: {ex.Message}", ex);
                return null;
            }
        }
        return null;
    }

    private async Task TrySetFirstContactDateAsync(string baseUrl, string token, string leadId, DateTime callStartTime)
    {
        try
        {
            var (createdAt, isFieldAlreadySet) =
                await GetLeadDetailsAsync(baseUrl, token, leadId);

            if (isFieldAlreadySet)
            {
                Debug.WriteLine("[Kommo] First-contact field already has a value — skipping");
                return;
            }

            long unix = new DateTimeOffset(callStartTime.ToUniversalTime()).ToUnixTimeSeconds();
            await PatchLeadFieldAsync(baseUrl, token, leadId, FirstContactFieldId, unix);

            if (createdAt.HasValue)
            {
                var leadCreated = DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).UtcDateTime;
                int minutes     = Math.Max(0, (int)Math.Round((leadCreated - callStartTime.ToUniversalTime()).TotalMinutes));
                Debug.WriteLine($"[Kommo] Processing speed: {minutes} min (created={leadCreated:u}, callStart={callStartTime.ToUniversalTime():u})");
                await PatchLeadFieldAsync(baseUrl, token, leadId, ProcessingSpeedFieldId, minutes);
            }
            else
            {
                Debug.WriteLine("[Kommo] created_at not available — skipping processing speed");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] TrySetFirstContact failed: {ex.Message}"); }
    }

    private async Task<(long? createdAt, bool isFieldSet)> GetLeadDetailsAsync(
        string baseUrl, string token, string leadId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/leads/{leadId}?with=custom_fields");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? createdAt = root.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : null;

            bool isFieldSet = false;
            if (root.TryGetProperty("custom_fields_values", out var fields) &&
                fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("field_id", out var fid) ||
                        fid.GetInt64() != FirstContactFieldId) continue;

                    if (field.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        foreach (var v in vals.EnumerateArray())
                            if (v.TryGetProperty("value", out var val) &&
                                val.ValueKind != JsonValueKind.Null &&
                                !(val.ValueKind == JsonValueKind.Number && val.GetInt64() == 0))
                            { isFieldSet = true; break; }
                    break;
                }
            }

            return (createdAt, isFieldSet);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] GetLeadDetails failed: {ex.Message}");
            return (null, false);
        }
    }

    private async Task PatchLeadFieldAsync(string baseUrl, string token, string leadId, long fieldId, object value)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                custom_fields_values = new[]
                {
                    new { field_id = fieldId, values = new[] { new { value } } }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/leads/{leadId}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await _http.SendAsync(req);
            Debug.WriteLine($"[Kommo] PatchLead → {(int)res.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] PatchLeadField failed: {ex.Message}"); }
    }

    public void Dispose() => _http.Dispose();

    private static (string subdomain, string? leadId) ParseLeadUrl(string url)
    {
        try
        {
            var uri       = new Uri(url.Trim());
            var subdomain = uri.Host.Split('.')[0];
            var segments  = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? leadId = null;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i] == "detail") { leadId = segments[i + 1]; break; }
            }
            return (subdomain, leadId);
        }
        catch { return (string.Empty, null); }
    }
}
