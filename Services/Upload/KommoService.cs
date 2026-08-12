using PhoneNumbers;
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

    private const long FirstContactFieldId             = 1225821;
    private const long ProcessingSpeedFieldId          = 1225823;
    private const long CallTypeFieldId                 = 1226157;
    private const long ProcessingSpeedLocalTimeFieldId = 1227531;
    private const long ContactPhoneFieldId             = 458590;

    // "Источник" (звідки прийшов лід) і конкретний enum-варіант "Реактивация" всередині нього.
    // Для лідів із цим джерелом швидкість обробки не має сенсу (їх "первое касание" — це
    // повторний вихід на вже "теплого" клієнта, а не реакція на нове звернення) — тому замість
    // розрахунку пишемо нулі (див. TrySetFirstContactDateAsync). Перевіряємо саме enum_id
    // (ідентифікатор обраної опції), а не текст значення — це стійкіше до перейменування
    // варіанту в самому Kommo.
    private const long SourceFieldId      = 1220023;
    private const long ReactivationEnumId = 1028911;

    // Коли обрано тип дзвінка "... (ще не було спілкування)" — і "Недодзвон (ще не було
    // спілкування)" з CallReportView, і "Недозвон 1..4 (ще не було спілкування)" з
    // MissedCallReportView — переводимо лід у стадію "Недозвон" воронки "Відділ продажу ЕК"
    // (значення підтверджені живим GET /leads/pipelines саме для акаунту користувача —
    // не змінювати без повторної перевірки). Перевіряємо підрядком, а не точним рядком, тому
    // всі варіанти "ще не було спілкування" підпадають однаково; "вже було спілкування" статус не чіпає.
    private const long   NedozvonPipelineId              = 12703972;
    private const long   NedozvonStatusId                = 98056416;
    private const string NoCommunicationCallTypeMarker   = "ще не було спілкування";

    private static bool IsNoCommunicationCallType(string? callType) =>
        callType is not null && callType.Contains(NoCommunicationCallTypeMarker);

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

    // ProcessingSpeedMinutes/ProcessingSpeedWorkMinutes — ті самі числа, що патчаться в кастомні
    // поля Kommo (ProcessingSpeedFieldId/ProcessingSpeedLocalTimeFieldId) всередині
    // TrySetFirstContactDateAsync; повертаємо їх нагору, щоб викликач (MissedCallDeliveryService)
    // міг продублювати ті самі значення в Google Таблицю без повторного походу в Kommo API.
    public async Task<(long? NoteId, int? ProcessingSpeedMinutes, int? ProcessingSpeedWorkMinutes)> ProcessLeadAsync(
        string leadUrl, string noteText, DateTime? callStartTime = null, string? callType = null)
    {
        if (!IsEnabled) return (null, null, null);

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;

        if (leadId is null || string.IsNullOrEmpty(subdomain))
        {
            Debug.WriteLine($"[Kommo] Cannot parse lead URL: {leadUrl}");
            ErrorReporter.Report("KOMMO", $"Не вдалося розпарсити URL ліда: {leadUrl}");
            return (null, null, null);
        }

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        var noteTask     = PostNoteAsync(baseUrl, token, leadId, noteText);
        var dateTask     = callStartTime.HasValue
            ? TrySetFirstContactDateAsync(baseUrl, token, leadId, callStartTime.Value)
            : Task.FromResult<(int? Minutes, int? WorkMinutes)>((null, null));
        var callTypeTask = !string.IsNullOrWhiteSpace(callType)
            ? PatchLeadFieldAsync(baseUrl, token, leadId, CallTypeFieldId, (object)callType)
            : Task.CompletedTask;
        var statusTask   = IsNoCommunicationCallType(callType)
            ? TryAdvanceToNedozvonStatusAsync(baseUrl, token, leadId)
            : Task.CompletedTask;

        await Task.WhenAll(noteTask, dateTask, callTypeTask, statusTask);
        var (speedMinutes, speedWorkMinutes) = await dateTask;
        return (await noteTask, speedMinutes, speedWorkMinutes);
    }

    // Легкий довідник "Швидкості" — без повторної нотатки/типу дзвінка/статусу — для ліда, чиє
    // Kommo-доставлення (нотатка) вже відбулось раніше, але сама швидкість тоді порахувалась не
    // повністю (напр. лід ще не мав прив'язаного контакту/компанії, чи телефон не резолвився в
    // таймзону, чи стався тимчасовий збій мережі саме в той момент). Викликається
    // MissedCallDeliveryService на фоновому повторі, коли треба довиконати лише цю частину, не
    // дублюючи вже надіслану нотатку. TrySetFirstContactDateAsync сама нічого не перезаписує,
    // якщо поле вже "зайняте" (occupied) — тож повторний виклик безпечний.
    public async Task<(int? ProcessingSpeedMinutes, int? ProcessingSpeedWorkMinutes)> RecalculateProcessingSpeedAsync(
        string leadUrl, DateTime callStartTime)
    {
        if (!IsEnabled) return (null, null);

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;
        if (leadId is null || string.IsNullOrEmpty(subdomain)) return (null, null);

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        return await TrySetFirstContactDateAsync(baseUrl, token, leadId, callStartTime);
    }

    // Дістає номер телефону клієнта за посиланням на лід — для плаваючої плашки-скріншотера,
    // яка сама формує deep-link на месенджер за цим номером (перевикористовує ту саму логіку
    // пошуку контакту/компанії, що й TrySetLocalTimeProcessingSpeedAsync).
    public async Task<string?> GetClientPhoneAsync(string leadUrl)
    {
        if (!IsEnabled) return null;

        var (subdomain, leadId) = ParseLeadUrl(leadUrl);
        subdomain = string.IsNullOrEmpty(subdomain) ? _settings.KommoSubdomain : subdomain;
        if (leadId is null || string.IsNullOrEmpty(subdomain)) return null;

        string baseUrl = $"https://{subdomain}.kommo.com/api/v4";
        string token   = _settings.KommoApiToken;

        var (_, _, _, contactId, companyId, _, _, _, _, _) = await GetLeadDetailsAsync(baseUrl, token, leadId);

        string? phone = contactId is not null ? await GetContactPhoneAsync(baseUrl, token, contactId) : null;
        if (string.IsNullOrWhiteSpace(phone) && companyId is not null)
            phone = await GetCompanyPhoneAsync(baseUrl, token, companyId);

        return string.IsNullOrWhiteSpace(phone) ? null : phone;
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

        if (IsNoCommunicationCallType(callType))
            await TryAdvanceToNedozvonStatusAsync(baseUrl, token, leadId);

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
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] PostNote attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
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

    private async Task<(int? Minutes, int? WorkMinutes)> TrySetFirstContactDateAsync(string baseUrl, string token, string leadId, DateTime callStartTime)
    {
        try
        {
            var (createdAt, existingFirstContactUnix, firstContactOccupied, contactId, companyId,
                existingSpeedMinutes, speedMinutesOccupied, existingSpeedWorkMinutes, speedWorkMinutesOccupied,
                isReactivationSource) =
                await GetLeadDetailsAsync(baseUrl, token, leadId);

            // "Дата и время первого касания" — байдуже, яким саме значенням воно заповнене
            // (дата, число чи навіть текст): якщо там вже щось є, нічого не пишемо. Порожнє —
            // проставляємо. firstContactUnix потрібен лише для розрахунку швидкості нижче,
            // тож якщо поле зайняте нечисловим значенням — просто пропускаємо той розрахунок.
            long? firstContactUnix;

            if (firstContactOccupied)
            {
                Debug.WriteLine("[Kommo] First-contact field already has a value — keeping it as is");
                firstContactUnix = existingFirstContactUnix;
            }
            else
            {
                firstContactUnix = new DateTimeOffset(callStartTime.ToUniversalTime()).ToUnixTimeSeconds();
                await PatchLeadFieldAsync(baseUrl, token, leadId, FirstContactFieldId, firstContactUnix.Value);
            }

            // "Реактивация" — лід уже був у роботі раніше, тож "швидкість першого касання"
            // тут не показник (це не реакція на нове звернення) — обрахунки пропускаємо і
            // просто пишемо нулі (з тим самим захистом "не чіпаємо, якщо вже зайняте", що й
            // для решти джерел, — щоб не затерти значення, введене вручну).
            int? minutes;
            int? workMinutes;
            if (isReactivationSource)
            {
                minutes = existingSpeedMinutes;
                if (!speedMinutesOccupied)
                {
                    Debug.WriteLine("[Kommo] Source = Реактивация — skipping processing-speed calc, writing 0");
                    minutes = 0;
                    await PatchLeadFieldAsync(baseUrl, token, leadId, ProcessingSpeedFieldId, 0);
                }

                workMinutes = existingSpeedWorkMinutes;
                if (!speedWorkMinutesOccupied)
                {
                    Debug.WriteLine("[Kommo] Source = Реактивация — skipping working-hours processing-speed calc, writing 0");
                    workMinutes = 0;
                    await PatchLeadFieldAsync(baseUrl, token, leadId, ProcessingSpeedLocalTimeFieldId, 0);
                }
            }
            else
            {
                // "Скорость обработки в мин" — зайняте (будь-яким типом значення) — не чіпаємо
                // і повертаємо як є (щоб MissedCallDeliveryService міг продублювати те саме в
                // Google Таблицю); порожнє і є з чого порахувати — проставляємо.
                minutes = existingSpeedMinutes;
                if (!speedMinutesOccupied && createdAt.HasValue && firstContactUnix.HasValue)
                {
                    var leadCreatedUtc  = DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).UtcDateTime;
                    var firstContactUtc = DateTimeOffset.FromUnixTimeSeconds(firstContactUnix.Value).UtcDateTime;

                    minutes = (int)Math.Round(Math.Abs((leadCreatedUtc - firstContactUtc).TotalMinutes));
                    Debug.WriteLine($"[Kommo] Processing speed: {minutes} min (created={leadCreatedUtc:u}, firstContact={firstContactUtc:u})");
                    await PatchLeadFieldAsync(baseUrl, token, leadId, ProcessingSpeedFieldId, minutes);
                }
                else if (speedMinutesOccupied)
                {
                    Debug.WriteLine("[Kommo] Processing-speed field already has a value — keeping it as is");
                }

                // "Скорость обработки в рабочее время" — те саме: не чіпаємо, якщо вже зайняте.
                workMinutes = existingSpeedWorkMinutes;
                if (speedWorkMinutesOccupied)
                {
                    Debug.WriteLine("[Kommo] Working-hours processing-speed field already has a value — keeping it as is");
                }
                else if (createdAt.HasValue && firstContactUnix.HasValue)
                {
                    var leadCreatedUtc  = DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).UtcDateTime;
                    var firstContactUtc = DateTimeOffset.FromUnixTimeSeconds(firstContactUnix.Value).UtcDateTime;

                    if (contactId is not null || companyId is not null)
                        workMinutes = await TrySetLocalTimeProcessingSpeedAsync(baseUrl, token, leadId, contactId, companyId, leadCreatedUtc, firstContactUtc);
                    else
                        ErrorReporter.Report("KOMMO", $"Лід {leadId} без прив'язаного контакту чи компанії — поле 'Скорость обработки в рабочее время' не заповнено");
                }
            }

            if (!createdAt.HasValue || !firstContactUnix.HasValue)
                Debug.WriteLine("[Kommo] created_at / first-contact unavailable as numbers — skipping processing speed calc");

            return (minutes, workMinutes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] TrySetFirstContact failed: {ex.Message}");
            ErrorReporter.Report("KOMMO", $"TrySetFirstContact exception — лід {leadId}: {ex.Message}", ex);
            return (null, null);
        }
    }

    private async Task<int?> TrySetLocalTimeProcessingSpeedAsync(
        string baseUrl, string token, string leadId, string? contactId, string? companyId, DateTime leadCreatedUtc, DateTime callStartUtc)
    {
        string? phone = contactId is not null ? await GetContactPhoneAsync(baseUrl, token, contactId) : null;

        if (string.IsNullOrWhiteSpace(phone) && companyId is not null)
        {
            Debug.WriteLine("[Kommo] Contact has no phone — falling back to company phone");
            phone = await GetCompanyPhoneAsync(baseUrl, token, companyId);
        }

        if (string.IsNullOrWhiteSpace(phone))
        {
            Debug.WriteLine("[Kommo] Neither contact nor company has a phone — skipping local-time processing speed");
            ErrorReporter.Report("KOMMO", $"Контакт {contactId ?? "—"} і компанія {companyId ?? "—"} (лід {leadId}) без телефону — поле 'Скорость обработки в рабочее время' не заповнено");
            return null;
        }

        var (timeZone, isUkrainian) = TryResolveTimeZoneFromPhone(phone);
        if (timeZone is null)
        {
            Debug.WriteLine($"[Kommo] Could not resolve timezone for phone '{phone}' — skipping local-time processing speed");
            ErrorReporter.Report("KOMMO", $"Не вдалося визначити часовий пояс за номером '{phone}' (контакт {contactId}, лід {leadId}) — поле 'Скорость обработки в рабочее время' не заповнено");
            return null;
        }

        var leadCreatedLocal = TimeZoneInfo.ConvertTimeFromUtc(leadCreatedUtc, timeZone);
        var callStartLocal   = TimeZoneInfo.ConvertTimeFromUtc(callStartUtc, timeZone);

        // Для українських номерів вікно робочого часу розширюємо на +7 год в обидва боки
        // (напр. дефолт 9:00–21:00 → 16:00–04:00) — так домовились для укр. клієнтів.
        var workDayStart = _settings.WorkDayStart;
        var workDayEnd   = _settings.WorkDayEnd;
        if (isUkrainian)
        {
            workDayStart += TimeSpan.FromHours(7);
            workDayEnd   += TimeSpan.FromHours(7);
        }

        var workingDuration = CalculateWorkingHoursDuration(leadCreatedLocal, callStartLocal, workDayStart, workDayEnd);
        int minutes = (int)Math.Round(workingDuration.TotalMinutes);

        Debug.WriteLine($"[Kommo] Processing speed (working hours, {timeZone.Id}{(isUkrainian ? ", UA +7h window" : "")}): {minutes} min");
        await PatchLeadFieldAsync(baseUrl, token, leadId, ProcessingSpeedLocalTimeFieldId, minutes);
        return minutes;
    }

    // Sums only the portion of [startLocal, endLocal] that falls within the daily
    // [workDayStart, workDayEnd) window (in the same local time), skipping the overnight
    // gap for every calendar day the interval spans — every day counts the same, no weekends excluded.
    private static TimeSpan CalculateWorkingHoursDuration(DateTime startLocal, DateTime endLocal, TimeSpan workDayStart, TimeSpan workDayEnd)
    {
        if (endLocal < startLocal) (startLocal, endLocal) = (endLocal, startLocal);

        var total = TimeSpan.Zero;
        var day   = startLocal.Date;

        while (day <= endLocal.Date)
        {
            var windowStart = day + workDayStart;
            var windowEnd   = day + workDayEnd;

            var segmentStart = windowStart > startLocal ? windowStart : startLocal;
            var segmentEnd   = windowEnd   < endLocal   ? windowEnd   : endLocal;

            if (segmentEnd > segmentStart)
                total += segmentEnd - segmentStart;

            day = day.AddDays(1);
        }

        return total;
    }

    private static (TimeZoneInfo? Zone, bool IsUkrainian) TryResolveTimeZoneFromPhone(string rawPhone)
    {
        try
        {
            var phoneNumberUtil = PhoneNumberUtil.GetInstance();
            var timeZonesMapper = PhoneNumberToTimeZonesMapper.GetInstance();

            PhoneNumber phoneNumber;
            try
            {
                phoneNumber = phoneNumberUtil.Parse(rawPhone, null);
            }
            catch (NumberParseException) when (!rawPhone.TrimStart().StartsWith('+'))
            {
                var trimmed = rawPhone.TrimStart();
                phoneNumber = trimmed.StartsWith('0')
                    // Local Ukrainian national format with a trunk "0" (e.g. "0 (98) 721 26 42") —
                    // no country code, so parse with defaultRegion "UA" instead of blindly
                    // prepending "+" (which would produce an invalid "+0..." E.164 string, since no
                    // country calling code can start with "0").
                    ? phoneNumberUtil.Parse(trimmed, "UA")
                    // No "+" but the digits already look like they include a country calling code —
                    // just missing the leading "+".
                    : phoneNumberUtil.Parse("+" + trimmed, null);
            }

            // Skips the strict validity check and always returns a best-effort geographic guess
            // (a single "typical" zone even for non-geographic mobile prefixes), instead of
            // GetTimeZonesForNumber's exhaustive candidate list for ambiguous numbers.
            var timeZones = timeZonesMapper.GetTimeZonesForGeographicalNumber(phoneNumber);

            var ianaId = timeZones.FirstOrDefault();
            if (string.IsNullOrEmpty(ianaId) || ianaId == "Etc/Unknown") return (null, false);

            // Визначаємо "українськість" за резолвнутою таймзоною (Europe/Kyiv), а не за
            // кодом країни — номер може прийти і в форматі +380..., і в місцевому 0955...
            bool isUkrainian = ianaId is "Europe/Kyiv" or "Europe/Kiev";

            // США і Україна лишаються "як є" (реальний географічний пояс номера); для БУДЬ-ЯКОЇ
            // іншої країни рахуємо робочий час так, ніби клієнт у Маямі (America/New_York), а не
            // по його справжньому поясу.
            bool isUS = phoneNumberUtil.GetRegionCodeForNumber(phoneNumber) == "US";
            if (!isUS && !isUkrainian) ianaId = "America/New_York";

            try
            {
                return (TimeZoneInfo.FindSystemTimeZoneById(ianaId), isUkrainian);
            }
            catch (TimeZoneNotFoundException) when (ianaId == "Europe/Kyiv")
            {
                // Some Windows builds haven't picked up the 2022 IANA rename (Kiev -> Kyiv) in their ICU data.
                return (TimeZoneInfo.FindSystemTimeZoneById("Europe/Kiev"), isUkrainian);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] TryResolveTimeZoneFromPhone failed: {ex.Message}");
            ErrorReporter.Report("KOMMO", $"Помилка визначення часового поясу за номером '{rawPhone}': {ex.Message}", ex);
            return (null, false);
        }
    }

    private async Task<string?> GetContactPhoneAsync(string baseUrl, string token, string contactId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/contacts/{contactId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"GetContactPhone HTTP {(int)res.StatusCode} — контакт {contactId}: {snippet}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("custom_fields_values", out var fields) ||
                    fields.ValueKind != JsonValueKind.Array)
                    return null;

                // Поле "Телефон" (field_id = ContactPhoneFieldId) у Kommo — це multitext-поле:
                // може містити кілька записів номера різних типів (WORK, MOB, FAX, HOME, OTHER...).
                // Якщо перший запис порожній, а номер вказаний у наступному — беремо перший НЕпорожній.
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("field_id", out var fid) || fid.GetInt64() != ContactPhoneFieldId)
                        continue;

                    if (field.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        foreach (var v in vals.EnumerateArray())
                            if (v.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(val.GetString()))
                                return val.GetString();

                    break;
                }

                return null;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] GetContactPhone attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] GetContactPhone failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"GetContactPhone exception — контакт {contactId}: {ex.Message}", ex);
                return null;
            }
        }
        return null;
    }

    // Той самий телефон (field_id = ContactPhoneFieldId) буває заповнений не на контакті,
    // а на прив'язаній до ліда компанії (наприклад, коли контакт створився без номера,
    // а форма записала телефон у компанію). Викликається як фолбек, якщо в контакта пусто.
    private async Task<string?> GetCompanyPhoneAsync(string baseUrl, string token, string companyId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/companies/{companyId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"GetCompanyPhone HTTP {(int)res.StatusCode} — компанія {companyId}: {snippet}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("custom_fields_values", out var fields) ||
                    fields.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("field_id", out var fid) || fid.GetInt64() != ContactPhoneFieldId)
                        continue;

                    if (field.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        foreach (var v in vals.EnumerateArray())
                            if (v.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(val.GetString()))
                                return val.GetString();

                    break;
                }

                return null;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] GetCompanyPhone attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] GetCompanyPhone failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"GetCompanyPhone exception — компанія {companyId}: {ex.Message}", ex);
                return null;
            }
        }
        return null;
    }

    // Поле вважається "вже заповненим", якщо в ньому лежить БУДЬ-яке непорожнє значення —
    // байдуже, дата (число-таймстамп), число (хвилини) чи текст (хтось міг вручну ввести
    // значення в полі іншого типу). У такому разі просто нічого не пишемо в нього.
    private static bool TryGetOccupiedValue(JsonElement valuesArray, out JsonElement value)
    {
        foreach (var v in valuesArray.EnumerateArray())
        {
            if (!v.TryGetProperty("value", out var val)) continue;

            bool occupied = val.ValueKind switch
            {
                JsonValueKind.Number             => val.GetInt64() != 0,
                JsonValueKind.String              => !string.IsNullOrWhiteSpace(val.GetString()),
                JsonValueKind.True or JsonValueKind.False => true,
                _                                  => false,
            };

            if (occupied) { value = val; return true; }
        }

        value = default;
        return false;
    }

    private async Task<(long? createdAt, long? firstContactUnix, bool firstContactOccupied, string? contactId, string? companyId,
        int? speedMinutes, bool speedMinutesOccupied, int? speedWorkMinutes, bool speedWorkMinutesOccupied,
        bool isReactivationSource)> GetLeadDetailsAsync(
        string baseUrl, string token, string leadId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // "with=companies" необхідне окремо від "contacts" — інакше _embedded.companies
                // не приходить у відповіді взагалі, і companyId нижче завжди лишається null для
                // лідів, прив'язаних лише до компанії (без прямого контакту).
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/leads/{leadId}?with=contacts,companies,custom_fields");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"GetLeadDetails HTTP {(int)res.StatusCode} — лід {leadId}: {snippet}");
                    return (null, null, false, null, null, null, false, null, false, false);
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                long? createdAt = root.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : null;

                long? firstContactUnix          = null;
                bool  firstContactOccupied      = false;
                int?  speedMinutes              = null;
                bool  speedMinutesOccupied      = false;
                int?  speedWorkMinutes          = null;
                bool  speedWorkMinutesOccupied  = false;
                bool  isReactivationSource      = false;
                if (root.TryGetProperty("custom_fields_values", out var fields) &&
                    fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        if (!field.TryGetProperty("field_id", out var fid)) continue;
                        long id = fid.GetInt64();

                        // "Источник" — окремий випадок: цікавить не value (текст), а enum_id
                        // (яка саме опція обрана) — перевіряємо його прямо тут, до TryGetOccupiedValue
                        // нижче (та розрахована на числові/текстові поля швидкості, а не на select).
                        if (id == SourceFieldId)
                        {
                            if (field.TryGetProperty("values", out var sourceVals) &&
                                sourceVals.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var sv in sourceVals.EnumerateArray())
                                {
                                    if (sv.TryGetProperty("enum_id", out var enumIdProp) &&
                                        enumIdProp.GetInt64() == ReactivationEnumId)
                                    {
                                        isReactivationSource = true;
                                        break;
                                    }
                                }
                            }
                            continue;
                        }

                        if (id != FirstContactFieldId && id != ProcessingSpeedFieldId && id != ProcessingSpeedLocalTimeFieldId)
                            continue;

                        if (!field.TryGetProperty("values", out var vals) || vals.ValueKind != JsonValueKind.Array)
                            continue;

                        if (!TryGetOccupiedValue(vals, out var val)) continue;

                        // Числові кастомні поля Kommo ("Скорость обработки в мин"/"...в рабочее время")
                        // часто повертаються як РЯДОК ("value": "15"), а не як JSON-число — особливо
                        // якщо значення колись проставили вручну в інтерфейсі Kommo, а не через API.
                        // TryGetOccupiedValue вище й так вважає такий рядок "зайнятим" полем, але без
                        // цього парсингу саме числове значення губилось (лишалось null), тож воно не
                        // потрапляло в Google Таблицю, хоча в CRM вже реально стояло.
                        long? numericValue = val.ValueKind switch
                        {
                            JsonValueKind.Number                                      => val.GetInt64(),
                            JsonValueKind.String when long.TryParse(val.GetString(), out var parsed) => parsed,
                            _                                                          => null,
                        };

                        if (id == FirstContactFieldId)
                        {
                            firstContactOccupied = true;
                            firstContactUnix     = numericValue;
                        }
                        else if (id == ProcessingSpeedFieldId)
                        {
                            speedMinutesOccupied = true;
                            speedMinutes         = (int?)numericValue;
                        }
                        else if (id == ProcessingSpeedLocalTimeFieldId)
                        {
                            speedWorkMinutesOccupied = true;
                            speedWorkMinutes         = (int?)numericValue;
                        }
                    }
                }

                string? contactId = null;
                string? companyId = null;
                if (root.TryGetProperty("_embedded", out var emb))
                {
                    if (emb.TryGetProperty("contacts", out var contacts) &&
                        contacts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in contacts.EnumerateArray())
                        {
                            if (!c.TryGetProperty("id", out var idProp)) continue;
                            bool isMain = c.TryGetProperty("is_main", out var im) && im.ValueKind == JsonValueKind.True;
                            if (isMain || contactId is null)
                                contactId = idProp.GetInt64().ToString();
                            if (isMain) break;
                        }
                    }

                    if (emb.TryGetProperty("companies", out var companies) &&
                        companies.ValueKind == JsonValueKind.Array &&
                        companies.GetArrayLength() > 0 &&
                        companies[0].TryGetProperty("id", out var companyIdProp))
                        companyId = companyIdProp.GetInt64().ToString();
                }

                return (createdAt, firstContactUnix, firstContactOccupied, contactId, companyId,
                    speedMinutes, speedMinutesOccupied, speedWorkMinutes, speedWorkMinutesOccupied,
                    isReactivationSource);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] GetLeadDetails attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] GetLeadDetails failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"GetLeadDetails exception — лід {leadId}: {ex.Message}", ex);
                return (null, null, false, null, null, null, false, null, false, false);
            }
        }
        return (null, null, false, null, null, null, false, null, false, false);
    }

    private async Task PatchLeadFieldAsync(string baseUrl, string token, string leadId, long fieldId, object value)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
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

                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Kommo] PatchLead field {fieldId} → {(int)res.StatusCode} (attempt {attempt})");

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"PatchLeadField HTTP {(int)res.StatusCode} — лід {leadId}, поле {fieldId}: {snippet}");
                }
                return;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] PatchLeadField attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] PatchLeadField failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"PatchLeadField exception — лід {leadId}, поле {fieldId}: {ex.Message}", ex);
                return;
            }
        }
    }

    // Угода не повинна рухатись назад по воронці: якщо вона вже в тій самій воронці
    // "Відділ продажу ЕК", але на стадії, що йде ПІСЛЯ "Недозвону" (менеджер вже просунув
    // угоду далі — наприклад, додзвонився і провів переговори), пропускаємо зміну статусу.
    // Якщо угода в іншій воронці (ще не потрапляла в цю) або на попередній/тій самій стадії —
    // переводимо в "Недозвон" як і раніше. При будь-якій помилці визначення поточної позиції —
    // не блокуємо: застосовуємо статус як і до цієї зміни (безпечніший дефолт, ніж мовчки нічого не зробити).
    private async Task TryAdvanceToNedozvonStatusAsync(string baseUrl, string token, string leadId)
    {
        try
        {
            var (currentPipelineId, currentStatusId) = await GetLeadPipelineStatusAsync(baseUrl, token, leadId);

            if (currentPipelineId == NedozvonPipelineId &&
                currentStatusId.HasValue &&
                currentStatusId.Value != NedozvonStatusId)
            {
                var sortById = await GetPipelineStatusSortOrderAsync(baseUrl, token, NedozvonPipelineId);

                if (sortById is not null &&
                    sortById.TryGetValue(currentStatusId.Value, out var currentSort) &&
                    sortById.TryGetValue(NedozvonStatusId, out var nedozvonSort) &&
                    currentSort > nedozvonSort)
                {
                    Debug.WriteLine($"[Kommo] Лід {leadId} вже на стадії {currentStatusId} (sort {currentSort}), що йде після 'Недозвону' (sort {nedozvonSort}) — статус не змінюємо");
                    return;
                }
            }

            await PatchLeadStatusAsync(baseUrl, token, leadId, NedozvonStatusId, NedozvonPipelineId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kommo] TryAdvanceToNedozvonStatus failed: {ex.Message} — застосовуємо статус як є");
            await PatchLeadStatusAsync(baseUrl, token, leadId, NedozvonStatusId, NedozvonPipelineId);
        }
    }

    private async Task<(long? pipelineId, long? statusId)> GetLeadPipelineStatusAsync(string baseUrl, string token, string leadId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/leads/{leadId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"GetLeadPipelineStatus HTTP {(int)res.StatusCode} — лід {leadId}: {snippet}");
                    return (null, null);
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                long? pipelineId = root.TryGetProperty("pipeline_id", out var pid) ? pid.GetInt64() : null;
                long? statusId   = root.TryGetProperty("status_id", out var sid) ? sid.GetInt64() : null;
                return (pipelineId, statusId);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] GetLeadPipelineStatus attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] GetLeadPipelineStatus failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"GetLeadPipelineStatus виняток — лід {leadId}: {ex.Message}", ex);
                return (null, null);
            }
        }
        return (null, null);
    }

    // sort — офіційне поле Kommo, що визначає порядок стадій у воронці (менше значення = раніше).
    private async Task<Dictionary<long, int>?> GetPipelineStatusSortOrderAsync(string baseUrl, string token, long pipelineId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/leads/pipelines/{pipelineId}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"GetPipelineStatusSortOrder HTTP {(int)res.StatusCode} — воронка {pipelineId}: {snippet}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("_embedded", out var emb) ||
                    !emb.TryGetProperty("statuses", out var statuses) ||
                    statuses.ValueKind != JsonValueKind.Array)
                    return null;

                var result = new Dictionary<long, int>();
                foreach (var s in statuses.EnumerateArray())
                    if (s.TryGetProperty("id", out var idProp) && s.TryGetProperty("sort", out var sortProp))
                        result[idProp.GetInt64()] = sortProp.GetInt32();

                return result;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] GetPipelineStatusSortOrder attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] GetPipelineStatusSortOrder failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"GetPipelineStatusSortOrder виняток — воронка {pipelineId}: {ex.Message}", ex);
                return null;
            }
        }
        return null;
    }

    // Переводить лід у вказану стадію конкретної воронки. На відміну від PatchLeadFieldAsync
    // (custom_fields_values), тут status_id/pipeline_id — поля верхнього рівня самого ліда.
    private async Task PatchLeadStatusAsync(string baseUrl, string token, string leadId, long statusId, long pipelineId)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { status_id = statusId, pipeline_id = pipelineId });

                using var req = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/leads/{leadId}")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var res  = await _http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Kommo] PatchLeadStatus {statusId}/{pipelineId} → {(int)res.StatusCode} (attempt {attempt})");

                if (!res.IsSuccessStatusCode)
                {
                    var snippet = body.Length > 300 ? body[..300] : body;
                    ErrorReporter.Report("KOMMO", $"PatchLeadStatus HTTP {(int)res.StatusCode} — лід {leadId}: {snippet}");
                }
                return;
            }
            catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < maxAttempts)
            {
                Debug.WriteLine($"[Kommo] PatchLeadStatus attempt {attempt} failed: {ex.Message} — retrying");
                await RetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Kommo] PatchLeadStatus failed: {ex.Message}");
                ErrorReporter.Report("KOMMO", $"PatchLeadStatus exception — лід {leadId}: {ex.Message}", ex);
                return;
            }
        }
    }

    // JsonException (у т.ч. JsonReaderException) додано поруч із мережевими помилками:
    // Kommo API інколи повертає успішний HTTP-статус з ПОРОЖНІМ або обірваним тілом
    // (короткочасний глюк на їхньому боці — таймаут проксі під навантаженням тощо),
    // і без цього повторної спроби для такого випадку не було — метод здавався одразу
    // з першої ж спроби, хоча повторний запит за секунди зазвичай повертає валідний JSON.
    private static bool IsTransientNetworkError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or JsonException;

    // Exponential backoff between retry attempts (2s, 4s for the current maxAttempts=3 call sites).
    private static Task RetryDelayAsync(int attempt) => Task.Delay(TimeSpan.FromSeconds(2 * Math.Pow(2, attempt - 1)));

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
