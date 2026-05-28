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

    private Dictionary<(long pid, long sid), (string pipeline, string status)>? _pipelineCache;
    private long? _firstContactFieldId;
    private long? _processingSpeedFieldId;
    private long? _leadSourceFieldId;
    private long? _callTypeFieldId;
    private bool  _fieldIdsLoaded;

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

    public async Task<long?> ProcessLeadAsync(string leadUrl, string noteText, DateTime? callStartTime = null, string? leadSource = null, string? callType = null)
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

        await EnsureAllFieldIdsAsync(baseUrl, token);

        bool skipDates = KommoRules.ShouldSkipDates(leadSource);
        if (skipDates)
            Debug.WriteLine($"[Kommo] Skipping first-contact/speed fields for source '{leadSource}'");

        var noteTask     = PostNoteAsync(baseUrl, token, leadId, noteText);
        var dateTask     = callStartTime.HasValue && !skipDates
            ? TrySetFirstContactDateAsync(baseUrl, token, leadId, callStartTime.Value)
            : Task.CompletedTask;
        var callTypeTask = !string.IsNullOrWhiteSpace(callType)
            ? SetCallTypeAsync(baseUrl, token, leadId, callType)
            : Task.CompletedTask;

        await Task.WhenAll(noteTask, dateTask, callTypeTask);
        return await noteTask;
    }

    public async Task<string?> EditNoteAsync(string leadUrl, long noteId, string noteText)
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
            return res.IsSuccessStatusCode ? null : $"Kommo: помилка {(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] EditNote failed: {ex.Message}");
            return $"Kommo: {ex.Message}";
        }
    }

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

            if (!res.IsSuccessStatusCode)
            {
                ErrorReporter.Report("KOMMO", $"PostNote HTTP {(int)res.StatusCode} — lead {leadId}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("_embedded", out var emb) &&
                emb.TryGetProperty("notes", out var notes) &&
                notes.GetArrayLength() > 0 &&
                notes[0].TryGetProperty("id", out var idProp))
                return idProp.GetInt64();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] PostNote failed: {ex.Message}");
            ErrorReporter.Report("KOMMO", $"PostNote exception — lead {leadId}: {ex.Message}", ex);
        }
        return null;
    }

    private async Task TrySetFirstContactDateAsync(string baseUrl, string token, string leadId, DateTime callStartTime)
    {
        try
        {
            long? fieldId = _firstContactFieldId;
            if (fieldId is null)
            {
                Debug.WriteLine("[Kommo] 'Дата и время первого касания' field not found");
                return;
            }

            long? sourceFieldId = _leadSourceFieldId;
            var (pipelineId, statusId, createdAt, isFieldAlreadySet, crmSource) =
                await GetLeadDetailsAsync(baseUrl, token, leadId, fieldId.Value, sourceFieldId);

            if (KommoRules.ShouldSkipDates(crmSource))
            {
                Debug.WriteLine($"[Kommo] Skipping first-contact/speed — CRM source '{crmSource}' is excluded");
                return;
            }

            if (isFieldAlreadySet)
            {
                Debug.WriteLine("[Kommo] First-contact field already has a value — skipping");
                return;
            }

            if (pipelineId is null || statusId is null) return;

            var (pipelineName, statusName) = await ResolvePipelineStatusNamesAsync(
                baseUrl, token, pipelineId.Value, statusId.Value);
            Debug.WriteLine($"[Kommo] Lead pipeline='{pipelineName}' status='{statusName}'");

            if (!KommoRules.ShouldSetFirstContact(pipelineName, statusName))
            {
                Debug.WriteLine("[Kommo] No first-contact rule matched — skipping date update");
                return;
            }

            long unix = new DateTimeOffset(callStartTime.ToUniversalTime()).ToUnixTimeSeconds();
            await PatchLeadFieldAsync(baseUrl, token, leadId, fieldId.Value, unix);

            if (createdAt.HasValue)
            {
                long? speedFieldId = _processingSpeedFieldId;
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

    private async Task<(long? pipelineId, long? statusId, long? createdAt, bool isFieldSet, string? sourceValue)> GetLeadDetailsAsync(
        string baseUrl, string token, string leadId, long fieldId, long? sourceFieldId = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/leads/{leadId}?with=custom_fields");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (null, null, null, false, null);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? pid       = root.TryGetProperty("pipeline_id", out var p)  ? p.GetInt64()  : null;
            long? sid       = root.TryGetProperty("status_id",   out var s)  ? s.GetInt64()  : null;
            long? createdAt = root.TryGetProperty("created_at",  out var ca) ? ca.GetInt64() : null;

            bool    isFieldSet  = false;
            string? sourceValue = null;

            if (root.TryGetProperty("custom_fields_values", out var fields) &&
                fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("field_id", out var fid)) continue;
                    long fldId = fid.GetInt64();

                    if (fldId == fieldId)
                    {
                        if (field.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                            foreach (var v in vals.EnumerateArray())
                                if (v.TryGetProperty("value", out var val) &&
                                    val.ValueKind != JsonValueKind.Null &&
                                    !(val.ValueKind == JsonValueKind.Number && val.GetInt64() == 0))
                                { isFieldSet = true; break; }
                    }
                    else if (sourceFieldId.HasValue && fldId == sourceFieldId.Value)
                    {
                        if (field.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                            foreach (var v in vals.EnumerateArray())
                                if (v.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                                { sourceValue = val.GetString(); break; }
                    }
                }
            }

            return (pid, sid, createdAt, isFieldSet, sourceValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] GetLeadDetails failed: {ex.Message}");
            return (null, null, null, false, null);
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

    private async Task EnsureAllFieldIdsAsync(string baseUrl, string token)
    {
        if (_fieldIdsLoaded) return;
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
                    if (!field.TryGetProperty("name", out var n)) continue;
                    var name = n.GetString();
                    if (name is null) continue;
                    long id = field.GetProperty("id").GetInt64();

                    if (!_firstContactFieldId.HasValue &&
                        (name.Contains("первого касания",  StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("першого контакту", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("першого касання",  StringComparison.OrdinalIgnoreCase)))
                        _firstContactFieldId = id;

                    else if (!_processingSpeedFieldId.HasValue &&
                        (name.Contains("Скорость обработки", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Швидкість обробки",  StringComparison.OrdinalIgnoreCase)))
                        _processingSpeedFieldId = id;

                    else if (!_leadSourceFieldId.HasValue &&
                        (name.Equals("Источник",      StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Джерело",       StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Источник лида", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Джерело ліда",  StringComparison.OrdinalIgnoreCase)))
                        _leadSourceFieldId = id;

                    else if (!_callTypeFieldId.HasValue &&
                        (name.Equals("Тип дзвінка",  StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Тип дзвінку",  StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Тип дзвонка",  StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Тип звонка",   StringComparison.OrdinalIgnoreCase)))
                        _callTypeFieldId = id;
                }

                if (!anyField) break;
                page++;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] EnsureAllFieldIds failed: {ex.Message}"); }
        finally { _fieldIdsLoaded = true; }
    }

    private async Task SetCallTypeAsync(string baseUrl, string token, string leadId, string callType)
    {
        try
        {
            if (_callTypeFieldId is null)
            {
                Debug.WriteLine("[Kommo] 'Тип дзвінка' field not found");
                NotificationService.ShowError("Kommo: поле «Тип дзвінка» не знайдено в CRM. Створіть його у вкладці «Технічні».");
                return;
            }
            await PatchLeadFieldAsync(baseUrl, token, leadId, _callTypeFieldId.Value, (object)callType);
        }
        catch (Exception ex) { Debug.WriteLine($"[Kommo] SetCallType failed: {ex.Message}"); }
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
