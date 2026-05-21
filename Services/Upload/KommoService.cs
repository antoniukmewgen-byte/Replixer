using Replixer.Models;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Replixer.Services.Upload;

public class KommoService
{
    // Pipeline name → allowed status names that trigger first-contact update
    private static readonly Dictionary<string, HashSet<string>> FirstContactRules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MN EB1/2 Квалификация"] = new(StringComparer.OrdinalIgnoreCase) { "Квалификация" },
        ["Відділ продажу ЕК"]     = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
        ["Квалификация"]          = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
    };

    private readonly AppSettings _settings;
    private readonly HttpClient  _http = new();

    // Simple in-memory cache per session
    private Dictionary<(long pid, long sid), (string pipeline, string status)>? _pipelineCache;
    private long? _firstContactFieldId;
    private long? _processingSpeedFieldId;

    public KommoService(AppSettings settings) => _settings = settings;

    public bool IsEnabled =>
        _settings.IsKommoEnabled &&
        !string.IsNullOrWhiteSpace(_settings.KommoApiToken) &&
        !string.IsNullOrWhiteSpace(_settings.KommoSubdomain);

    // ── Public API ────────────────────────────────────────────────────────────

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

    /// <summary>Posts note + conditionally sets first-contact date. Returns the created note ID.</summary>
    public async Task<long?> ProcessLeadAsync(string leadUrl, string noteText, DateTime? callStartTime = null)
    {
        if (!IsEnabled) return null;

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;

        if (leadId is null || string.IsNullOrEmpty(subdomain))
        {
            Debug.WriteLine($"[Kommo] Cannot parse lead URL: {leadUrl}");
            return null;
        }

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        var noteTask = PostNoteAsync(baseUrl, token, leadId, noteText);
        var dateTask = callStartTime.HasValue
            ? TrySetFirstContactDateAsync(baseUrl, token, leadId, callStartTime.Value)
            : Task.CompletedTask;

        await Task.WhenAll(noteTask, dateTask);
        return await noteTask;
    }

    /// <summary>Updates the text of an existing note.</summary>
    public async Task<bool> EditNoteAsync(string leadUrl, long noteId, string noteText)
    {
        if (!IsEnabled) return false;

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;

        if (leadId is null || string.IsNullOrEmpty(subdomain))
        {
            Debug.WriteLine($"[Kommo] Cannot parse lead URL: {leadUrl}");
            return false;
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
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] EditNote failed: {ex.Message}");
            return false;
        }
    }

    // ── Private: note ─────────────────────────────────────────────────────────

    private async Task<long?> PostNoteAsync(string baseUrl, string token, string leadId, string text)
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
            Debug.WriteLine($"[Kommo] Note POST → {(int)res.StatusCode}");

            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("_embedded", out var emb) &&
                emb.TryGetProperty("notes", out var notes) &&
                notes.GetArrayLength() > 0 &&
                notes[0].TryGetProperty("id", out var idProp))
                return idProp.GetInt64();
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] PostNote failed: {ex.Message}"); }
        return null;
    }

    // ── Private: first-contact date ───────────────────────────────────────────

    private async Task TrySetFirstContactDateAsync(string baseUrl, string token, string leadId, DateTime callStartTime)
    {
        try
        {
            // 1. Get field IDs (cached after first call)
            long? fieldId = await GetFirstContactFieldIdAsync(baseUrl, token);
            if (fieldId is null)
            {
                Debug.WriteLine("[Kommo] 'Дата и время первого касания' field not found");
                return;
            }

            // 2. Get lead details: pipeline, status, createdAt, and current field value — one request
            var (pipelineId, statusId, createdAt, isFieldAlreadySet) =
                await GetLeadDetailsAsync(baseUrl, token, leadId, fieldId.Value);

            if (isFieldAlreadySet)
            {
                Debug.WriteLine("[Kommo] First-contact field already has a value — skipping");
                return;
            }

            if (pipelineId is null || statusId is null) return;

            // 3. Resolve pipeline/status names (cached)
            var (pipelineName, statusName) = await ResolvePipelineStatusNamesAsync(
                baseUrl, token, pipelineId.Value, statusId.Value);
            Debug.WriteLine($"[Kommo] Lead pipeline='{pipelineName}' status='{statusName}'");

            // 4. Check rules
            if (!ShouldSetFirstContact(pipelineName, statusName))
            {
                Debug.WriteLine("[Kommo] No first-contact rule matched — skipping date update");
                return;
            }

            // 5. Set first-contact date
            long unix = new DateTimeOffset(callStartTime.ToUniversalTime()).ToUnixTimeSeconds();
            await PatchLeadFieldAsync(baseUrl, token, leadId, fieldId.Value, unix);

            // 6. Calculate and set processing speed (minutes since lead creation)
            if (createdAt.HasValue)
            {
                long? speedFieldId = await GetProcessingSpeedFieldIdAsync(baseUrl, token);
                if (speedFieldId is not null)
                {
                    var leadCreated = DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).UtcDateTime;
                    int minutes     = (int)Math.Round((callStartTime.ToUniversalTime() - leadCreated).TotalMinutes);
                    Debug.WriteLine($"[Kommo] Processing speed: {minutes} min (created={leadCreated:u}, callStart={callStartTime.ToUniversalTime():u})");
                    await PatchLeadFieldAsync(baseUrl, token, leadId, speedFieldId.Value, minutes);
                }
                else
                {
                    Debug.WriteLine("[Kommo] 'Скорость обработки в мин.' field not found — skipping");
                }
            }
            else
            {
                Debug.WriteLine("[Kommo] created_at not available — skipping processing speed");
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] TrySetFirstContact failed: {ex.Message}"); }
    }

    private async Task<(long? pipelineId, long? statusId, long? createdAt, bool isFieldSet)> GetLeadDetailsAsync(
        string baseUrl, string token, string leadId, long fieldId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/leads/{leadId}?with=custom_fields");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, null, null, false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? pid       = root.TryGetProperty("pipeline_id", out var p)  ? p.GetInt64()  : null;
            long? sid       = root.TryGetProperty("status_id",   out var s)  ? s.GetInt64()  : null;
            long? createdAt = root.TryGetProperty("created_at",  out var ca) ? ca.GetInt64() : null;

            // Check if first-contact field already has a value
            bool isFieldSet = false;
            if (root.TryGetProperty("custom_fields_values", out var fields) &&
                fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("field_id", out var fid)) continue;
                    if (fid.GetInt64() != fieldId) continue;

                    if (field.TryGetProperty("values", out var vals) &&
                        vals.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in vals.EnumerateArray())
                        {
                            if (v.TryGetProperty("value", out var val) &&
                                val.ValueKind != JsonValueKind.Null &&
                                !(val.ValueKind == JsonValueKind.Number && val.GetInt64() == 0))
                            {
                                isFieldSet = true;
                                break;
                            }
                        }
                    }
                    break;
                }
            }

            return (pid, sid, createdAt, isFieldSet);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] GetLeadDetails failed: {ex.Message}");
            return (null, null, null, false);
        }
    }

    private async Task<(string pipeline, string status)> ResolvePipelineStatusNamesAsync(
        string baseUrl, string token, long pipelineId, long statusId)
    {
        if (_pipelineCache is null)
            _pipelineCache = await FetchPipelineCacheAsync(baseUrl, token);

        if (_pipelineCache.TryGetValue((pipelineId, statusId), out var names))
            return names;

        Debug.WriteLine($"[Kommo] pipeline_id={pipelineId} status_id={statusId} not found in cache ({_pipelineCache.Count} entries)");
        return (string.Empty, string.Empty);
    }

    private async Task<Dictionary<(long, long), (string pipeline, string status)>> FetchPipelineCacheAsync(string baseUrl, string token)
    {
        var cache = new Dictionary<(long, long), (string, string)>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/leads/pipelines?limit=250");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            Debug.WriteLine($"[Kommo] Pipelines GET → {(int)res.StatusCode}");
            if (!res.IsSuccessStatusCode) return cache;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("_embedded", out var emb)) return cache;
            if (!emb.TryGetProperty("pipelines", out var pipelines))       return cache;

            foreach (var pipeline in pipelines.EnumerateArray())
            {
                long   pid   = pipeline.GetProperty("id").GetInt64();
                string pname = pipeline.GetProperty("name").GetString() ?? string.Empty;
                Debug.WriteLine($"[Kommo]   Pipeline id={pid} name='{pname}'");

                if (!pipeline.TryGetProperty("_embedded", out var pEmb)) continue;
                if (!pEmb.TryGetProperty("statuses", out var statuses))   continue;

                foreach (var status in statuses.EnumerateArray())
                {
                    long   sid   = status.GetProperty("id").GetInt64();
                    string sname = status.GetProperty("name").GetString() ?? string.Empty;
                    Debug.WriteLine($"[Kommo]     Status id={sid} name='{sname}'");
                    cache[(pid, sid)] = (pname, sname);
                }
            }
            Debug.WriteLine($"[Kommo] Pipeline cache loaded: {cache.Count} entries");
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] FetchPipelineCache failed: {ex.Message}"); }
        return cache;
    }

    private static bool ShouldSetFirstContact(string pipelineName, string statusName)
    {
        foreach (var (pipeline, allowedStatuses) in FirstContactRules)
        {
            if (pipelineName.Equals(pipeline, StringComparison.OrdinalIgnoreCase) &&
                allowedStatuses.Contains(statusName))
                return true;
        }
        return false;
    }

    private async Task<long?> GetFirstContactFieldIdAsync(string baseUrl, string token)
    {
        if (_firstContactFieldId.HasValue) return _firstContactFieldId;
        try
        {
            int page = 1;
            while (true)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/leads/custom_fields?limit=250&page={page}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Kommo] CustomFields GET page={page} → {(int)res.StatusCode}");
                if (!res.IsSuccessStatusCode) break;

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("_embedded", out var emb)) break;
                if (!emb.TryGetProperty("custom_fields", out var fields))      break;

                bool anyField = false;
                foreach (var field in fields.EnumerateArray())
                {
                    anyField = true;
                    var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null) continue;
                    Debug.WriteLine($"[Kommo]   Field id={field.GetProperty("id").GetInt64()} name='{name}'");

                    if (name.Contains("первого касания",  StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("першого контакту", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("першого касання",  StringComparison.OrdinalIgnoreCase))
                    {
                        _firstContactFieldId = field.GetProperty("id").GetInt64();
                        Debug.WriteLine($"[Kommo] ✓ Found first-contact field id={_firstContactFieldId}");
                        return _firstContactFieldId;
                    }
                }

                if (!anyField) break; // no more pages
                page++;
            }
            Debug.WriteLine("[Kommo] ✗ First-contact field not found in any page");
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] GetFirstContactField failed: {ex.Message}"); }
        return null;
    }

    private async Task<long?> GetProcessingSpeedFieldIdAsync(string baseUrl, string token)
    {
        if (_processingSpeedFieldId.HasValue) return _processingSpeedFieldId;
        try
        {
            int page = 1;
            while (true)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/leads/custom_fields?limit=250&page={page}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) break;

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("_embedded", out var emb)) break;
                if (!emb.TryGetProperty("custom_fields", out var fields))      break;

                bool anyField = false;
                foreach (var field in fields.EnumerateArray())
                {
                    anyField = true;
                    var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null) continue;

                    if (name.Contains("Скорость обработки", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Швидкість обробки",  StringComparison.OrdinalIgnoreCase))
                    {
                        _processingSpeedFieldId = field.GetProperty("id").GetInt64();
                        Debug.WriteLine($"[Kommo] ✓ Found processing-speed field id={_processingSpeedFieldId} name='{name}'");
                        return _processingSpeedFieldId;
                    }
                }

                if (!anyField) break;
                page++;
            }
            Debug.WriteLine("[Kommo] ✗ Processing-speed field not found in any page");
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] GetProcessingSpeedField failed: {ex.Message}"); }
        return null;
    }

    private async Task PatchLeadFieldAsync(string baseUrl, string token, string leadId, long fieldId, long unixTimestamp)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                custom_fields_values = new[]
                {
                    new { field_id = fieldId, values = new[] { new { value = unixTimestamp } } }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/leads/{leadId}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var res = await _http.SendAsync(req);
            Debug.WriteLine($"[Kommo] PatchLead (first-contact) → {(int)res.StatusCode}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] PatchLeadField failed: {ex.Message}"); }
    }

    // ── URL parsing ───────────────────────────────────────────────────────────

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
