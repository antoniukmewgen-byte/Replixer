using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Replixer.Infrastructure;
using Replixer.Services;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Replixer.Services.Upload;

// Дублювання даних про недодзвін у Google Таблицю. Побудовано за тим самим принципом, що
// й GoogleDriveUploadService: той самий сервіс-акаунт (AppSecrets.GoogleServiceAccountJson),
// але окремий SheetsService зі своїм scope — таблицю, куди пишемо, власник має заздалегідь
// "розшарити" на email сервіс-акаунта як редактора, інакше API поверне 403.
public class GoogleSheetsUploadService
{
    // Фіксована частина шапки — відповідає перших 10 колонкам, які пише BuildSheetRow
    // (MissedCallDeliveryService). Далі йдуть "Скріншот N" — їх кількість залежить від
    // конкретного рядка (BuildSheetRow додає щонайменше 2, і більше, якщо скріншотів
    // фактично більше) — див. BuildHeaders.
    private static readonly string[] FixedSheetHeaders =
    {
        "Рік", "День", "Місяць", "Час запису", "Менеджер", "Тип дзвінка",
        "Дата дзвінка", "Швидкість, хв", "Швидкість (роб. час), хв",
        "Посилання CRM",
    };

    private readonly SheetsService? _service;

    public bool IsAuthorized => _service is not null;

    public GoogleSheetsUploadService()
    {
        try
        {
            _service = CreateService();
            Debug.WriteLine("[GSheets] Service account initialized successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSheets] Failed to initialize service account: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Перевіряє, що таблиця з таким ID доступна сервіс-акаунту і має аркуш з такою назвою.
    // Повертає null при успіху, інакше — текст помилки для показу в UI.
    public async Task<string?> TestAccessAsync(string spreadsheetId, string sheetName, CancellationToken ct = default)
    {
        Debug.WriteLine($"[GSheets] ── TestAccess ──────────────────────────");
        Debug.WriteLine($"[GSheets] Service initialized : {_service is not null}");
        Debug.WriteLine($"[GSheets] SpreadsheetId       : '{spreadsheetId}'");
        Debug.WriteLine($"[GSheets] SheetName           : '{sheetName}'");

        if (_service is null)
        {
            const string err = "service_account.json не знайдено або має невірний формат";
            Debug.WriteLine($"[GSheets] ✗ {err}");
            return err;
        }

        if (string.IsNullOrWhiteSpace(spreadsheetId))
        {
            const string err = "ID таблиці не вказано";
            Debug.WriteLine($"[GSheets] ✗ {err}");
            return err;
        }

        try
        {
            var req = _service.Spreadsheets.Get(spreadsheetId);
            req.Fields = "properties.title,sheets.properties.title";
            var spreadsheet = await req.ExecuteAsync(ct);

            bool hasSheet = !string.IsNullOrWhiteSpace(sheetName) &&
                spreadsheet.Sheets is not null &&
                spreadsheet.Sheets.Any(s => s.Properties?.Title == sheetName);

            if (!string.IsNullOrWhiteSpace(sheetName) && !hasSheet)
            {
                string err = $"Таблиця '{spreadsheet.Properties?.Title}' знайдена, але аркуша '{sheetName}' в ній немає";
                Debug.WriteLine($"[GSheets] ✗ {err}");
                return err;
            }

            Debug.WriteLine($"[GSheets] ✓ Spreadsheet found: '{spreadsheet.Properties?.Title}'");
            return null;
        }
        catch (Google.GoogleApiException apiEx)
        {
            string err = $"[{(int)apiEx.HttpStatusCode}] {apiEx.Error?.Message ?? apiEx.Message}";
            Debug.WriteLine($"[GSheets] ✗ API error: {err}");
            if (apiEx.Error?.Errors != null)
                foreach (var e in apiEx.Error.Errors)
                    Debug.WriteLine($"[GSheets]   Detail: {e.Reason} — {e.Message}");
            return err;
        }
        catch (Exception ex)
        {
            string err = $"{ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[GSheets] ✗ Exception: {err}");
            return err;
        }
        finally
        {
            Debug.WriteLine($"[GSheets] ───────────────────────────────────────────");
        }
    }

    // Дописує один рядок в кінець аркуша (перший вільний рядок після наявних даних).
    // Повертає null при успіху, інакше — текст помилки. isNetworkError даємо викликачу
    // (MissedCallDeliveryService), щоб він вирішував, чи ставити запис у фонову чергу повтору —
    // так само, як зараз влаштовано для Kommo.
    public async Task<(bool success, bool isNetworkError, string? error)> AppendRowAsync(
        string spreadsheetId, string sheetName, IList<object?> values, CancellationToken ct = default)
    {
        if (_service is null)
        {
            const string err = "Service account not initialized. Check that service_account.json is embedded.";
            Debug.WriteLine($"[GSheets] ✗ {err}");
            return (false, false, err);
        }

        if (string.IsNullOrWhiteSpace(spreadsheetId))
            return (false, false, "ID таблиці не вказано");

        await EnsureHeaderRowAsync(spreadsheetId, sheetName, values.Count, ct);

        try
        {
            var range     = string.IsNullOrWhiteSpace(sheetName) ? "A:A" : $"{sheetName}!A:A";
            var valueRange = new ValueRange { Values = new List<IList<object>> { (IList<object>)values! } };

            var req = _service.Spreadsheets.Values.Append(valueRange, spreadsheetId, range);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

            var result = await req.ExecuteAsync(ct);
            Debug.WriteLine($"[GSheets] ✓ Row appended: {result.Updates?.UpdatedRange}");
            return (true, false, null);
        }
        catch (Google.GoogleApiException apiEx)
        {
            string err = $"[{(int)apiEx.HttpStatusCode}] {apiEx.Error?.Message ?? apiEx.Message}";
            Debug.WriteLine($"[GSheets] ✗ API error: {err}");
            return (false, false, err);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Debug.WriteLine($"[GSheets] ✗ Network error: {ex.Message}");
            return (false, true, ex.Message);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Debug.WriteLine($"[GSheets] ✗ Socket error: {ex.Message}");
            return (false, true, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[GSheets] Append cancelled");
            return (false, false, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSheets] ✗ Exception: {ex.GetType().Name}: {ex.Message}");
            ErrorReporter.Report("GOOGLE_SHEETS", $"Append exception: {ex.Message}", ex);
            return (false, false, ex.Message);
        }
    }

    // Перевіряє перший рядок аркуша: якщо він порожній (нова/щойно створена таблиця) —
    // дописує повну шапку з назвами колонок (враховуючи, скільки саме колонок скріншотів
    // потрібно саме для цього рядка). Якщо шапка вже є, але коротша, ніж потрібно для
    // поточного рядка (напр. раніше було 2 "Скріншот N", а зараз недодзвін має 4 скріни) —
    // дописує лише бракуючий хвіст (нові "Скріншот N"), не чіпаючи вже наявні підписи.
    // Помилки тут — не фатальні: якщо перевірка/запис шапки не вдалися (напр. немає прав
    // саме на Update, хоча Append якимось дивом працює), просто пропускаємо крок і йдемо
    // далі дописувати сам рядок з даними.
    private async Task EnsureHeaderRowAsync(string spreadsheetId, string sheetName, int columnCount, CancellationToken ct)
    {
        try
        {
            var headers = BuildHeaders(columnCount);

            var checkRange = string.IsNullOrWhiteSpace(sheetName) ? "A1:ZZ1" : $"{sheetName}!A1:ZZ1";
            var getReq      = _service!.Spreadsheets.Values.Get(spreadsheetId, checkRange);
            var existing    = await getReq.ExecuteAsync(ct);
            var existingRow = existing.Values is { Count: > 0 } ? existing.Values[0] : null;

            bool hasHeader = existingRow is { Count: > 0 } && !string.IsNullOrWhiteSpace(existingRow[0]?.ToString());

            List<object> toWrite;
            string targetRange;

            if (!hasHeader)
            {
                // Шапки взагалі немає — пишемо всю з A1.
                toWrite     = headers.Cast<object>().ToList();
                targetRange = string.IsNullOrWhiteSpace(sheetName) ? "A1" : $"{sheetName}!A1";
            }
            else if (existingRow!.Count < headers.Count)
            {
                // Шапка коротша за потрібне (з'явились нові колонки скріншотів) — дописуємо хвіст.
                toWrite = headers.Skip(existingRow.Count).Cast<object>().ToList();
                string startColumn = ColumnLetter(existingRow.Count + 1);
                targetRange = string.IsNullOrWhiteSpace(sheetName) ? $"{startColumn}1" : $"{sheetName}!{startColumn}1";
            }
            else
            {
                return; // Шапка вже є і достатньо широка.
            }

            var valueRange = new ValueRange { Values = new List<IList<object>> { toWrite } };
            var updateReq  = _service.Spreadsheets.Values.Update(valueRange, spreadsheetId, targetRange);
            updateReq.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateReq.ExecuteAsync(ct);

            Debug.WriteLine(hasHeader ? "[GSheets] Header row extended" : "[GSheets] Header row created");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GSheets] EnsureHeaderRowAsync skipped (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Фіксовані 9 колонок + "Скріншот N" по кількості колонок скріншотів у цьому конкретному
    // рядку (columnCount — загальна довжина рядка з BuildSheetRow, мінімум 2 скріншоти).
    private static List<string> BuildHeaders(int columnCount)
    {
        var headers = FixedSheetHeaders.ToList();
        int screenshotColumns = Math.Max(2, columnCount - FixedSheetHeaders.Length);
        for (int i = 1; i <= screenshotColumns; i++)
            headers.Add($"Скріншот {i}");
        return headers;
    }

    // Переводить 1-based номер колонки в літерне позначення (1→A, 26→Z, 27→AA, ...).
    private static string ColumnLetter(int index)
    {
        var result = string.Empty;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            result = (char)('A' + rem) + result;
            index = (index - 1) / 26;
        }
        return result;
    }

    private static SheetsService CreateService()
    {
        var json = AppSecrets.GoogleServiceAccountJson;
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("GoogleServiceAccountJson is not configured in AppSecrets.");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var credential = GoogleCredential.FromStream(stream)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = "Replixer",
        });
    }
}
