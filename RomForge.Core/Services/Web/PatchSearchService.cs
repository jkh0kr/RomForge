using RomForge.Core.Models.Web;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RomForge.Core.Services.Web;

public class PatchSearchService
{
    private const string SpreadsheetId = "1tUBzLVdRf16goco_aATGOwN-DQCdU0nZXt--9hHRE0U";
    private const string SheetName = "전체";

    private const string AddPatchWebAppUrl = "https://script.google.com/macros/s/AKfycb.../exec";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<List<PatchEntry>> SearchAsync(DateTime? startDate, DateTime? endDate, string? system, string? keyword, CancellationToken ct = default)
    {
        var url = $"https://docs.google.com/spreadsheets/d/{SpreadsheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(SheetName)}";

        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var csv = await response.Content.ReadAsStringAsync(ct);
        var rows = ParseCsv(csv);

        var all = new List<PatchEntry>();

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.Count < 4 || string.IsNullOrWhiteSpace(row[0]))
                continue;

            all.Add(new PatchEntry
            {
                System = row.Count > 0 ? row[0] : "",
                Title = row.Count > 1 ? row[1] : "",
                Version = row.Count > 2 ? row[2] : "",
                Date = row.Count > 3 ? row[3] : "",
                Url = row.Count > 4 ? row[4] : ""
            });
        }

        var filtered = all.Where(r =>
        {
            if (!string.IsNullOrEmpty(system) && r.System != system)
                return false;

            if (startDate.HasValue || endDate.HasValue)
            {
                var rowDate = ParseSheetDate(r.Date);

                if (rowDate == null)
                    return false;

                if (startDate.HasValue && rowDate.Value.Date < startDate.Value.Date)
                    return false;

                if (endDate.HasValue && rowDate.Value.Date > endDate.Value.Date)
                    return false;
            }

            if (!string.IsNullOrEmpty(keyword) && r.Title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        });

        return [.. filtered
            .OrderByDescending(r => ParseSheetDate(r.Date) ?? DateTime.MinValue)
            .ThenBy(r => r.System, StringComparer.Ordinal)
            .ThenBy(r => r.Title, StringComparer.Ordinal)];
    }

    public static async Task<string> AddPatchAsync(PatchEntry entry, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(entry, JsonOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(AddPatchWebAppUrl, content, ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
        var message = root.TryGetProperty("message", out var messageProp) ? messageProp.GetString() ?? "" : "";

        if (!success)
            throw new InvalidOperationException(string.IsNullOrEmpty(message) ? "등록에 실패했습니다." : message);

        return message;
    }
    private static DateTime? ParseSheetDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var m = Regex.Match(value, @"(\d{4})\.\s*(\d{1,2})\.\s*(\d{1,2})");

        if (m.Success)
        {
            try
            {
                return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            }
            catch (ArgumentOutOfRangeException) { }
        }

        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed))
            return parsed;

        return null;
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < csv.Length; i++)
        {
            var c = csv[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}