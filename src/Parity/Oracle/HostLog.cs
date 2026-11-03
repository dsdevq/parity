using System.Globalization;
using System.Text.Json;

namespace Parity.Oracle;

/// <summary>
/// One row of the backend host's per-call log, as the adopter dumps it (<c>docs/oracle.md</c>, The host log). Category, type and
/// request are required; every other column is optional. Columns the format does not name are never read, so they never reach a
/// recording.
/// </summary>
public sealed record LogRow(int Order, DateTimeOffset? Time, string? Session, string Category, string Type, string Request,
    string? Reply, string? Dataset, string? Error, double? DurationMs);

public static class HostLog
{
    /// <summary>
    /// Reads a dump: JSON lines (one object per line) or one JSON array of objects. Keys are case-insensitive; <c>reply</c> and
    /// <c>dataset</c> may be a string (XML, JSON or text) or an inline JSON value, kept as its JSON text.
    /// </summary>
    public static List<LogRow> Read(string path, int orderBase = 0)
    {
        var text = File.ReadAllText(path).TrimStart('﻿');
        var name = Path.GetFileName(path);
        var objects = new List<(JsonElement Row, int Line)>();
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('['))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException ex) { throw new InvalidOperationException($"{name}: not a JSON array of log rows: {ex.Message}"); }
            var i = 0;
            foreach (var e in doc.RootElement.EnumerateArray()) objects.Add((e.Clone(), ++i));
        }
        else
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try { objects.Add((JsonDocument.Parse(lines[i]).RootElement.Clone(), i + 1)); }
                catch (JsonException ex) { throw new InvalidOperationException($"{name}:{i + 1}: not a JSON object: {ex.Message}"); }
            }
        }
        var rows = new List<LogRow>();
        foreach (var (o, line) in objects)
        {
            if (o.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{name}:{line}: a log row must be a JSON object");
            string? Get(string key)
            {
                foreach (var p in o.EnumerateObject())
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind switch
                        {
                            JsonValueKind.Null or JsonValueKind.Undefined => null,
                            JsonValueKind.String => p.Value.GetString(),
                            _ => p.Value.GetRawText(),
                        };
                return null;
            }
            var category = Get("category");
            var type = Get("type");
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(type))
                throw new InvalidOperationException($"{name}:{line}: a log row needs 'category' and 'type'");
            if (category.Contains('/') || type.Contains('/'))
                throw new InvalidOperationException($"{name}:{line}: 'category' and 'type' may not contain '/' (a call is named Category/Type)");
            DateTimeOffset? time = DateTimeOffset.TryParse(Get("time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            double? ms = double.TryParse(Get("durationMs"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            rows.Add(new LogRow(orderBase + rows.Count, time, Get("session"), category.Trim(), type.Trim(), Get("request") ?? "", Get("reply"), Get("dataset"), Get("error"), ms));
        }
        return rows;
    }

    /// <summary>
    /// Several dumps (a shared pre-login log and a per-tenant log, say) merged in call order: by time when every row has one, else in
    /// the order the files and rows are given.
    /// </summary>
    public static List<LogRow> ReadAll(IEnumerable<string> paths)
    {
        var all = new List<LogRow>();
        foreach (var p in paths) all.AddRange(Read(p, all.Count));
        return all.All(r => r.Time != null) ? all.OrderBy(r => r.Time).ThenBy(r => r.Order).ToList() : all;
    }
}
