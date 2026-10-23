using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Parity.Tools;

/// <summary>
/// Merges the kit's hooks block into a repo's own .claude/settings.json. The repo's entries stay where they are, in their
/// order; parity's are appended to each event's array. A hook the repo already has, by command or by script name, is not added twice.
/// </summary>
public static class SettingsMerge
{
    static readonly JsonSerializerOptions Out = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Comments and trailing commas are read; a duplicate key is refused, since which one counts is unclear.</summary>
    public static JsonObject Parse(string text, string what)
    {
        try { return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false }) as JsonObject ?? throw new InvalidOperationException($"{what} is not a JSON object"); }
        catch (JsonException ex) { throw new InvalidOperationException($"{what} is not valid JSON ({ex.Message})"); }
    }

    /// <summary>Whether rewriting the text loses nothing: no comments and no trailing commas.</summary>
    public static bool IsStrict(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Out) + "\n";

    /// <summary>The script a hook command runs (the last file name with a script extension), else the command itself.</summary>
    public static string ScriptKey(string command)
    {
        var m = Regex.Matches(command, @"[\w.\-]+\.(?:sh|cmd|bat|ps1|js|py)\b");
        return m.Count > 0 ? m[^1].Value : command;
    }

    /// <summary>The (event, matcher, command) of every hook the kit wants.</summary>
    public static List<(string Event, string? Matcher, string Command)> Wanted(JsonObject kit)
    {
        var list = new List<(string, string?, string)>();
        if (kit["hooks"] is not JsonObject hooks) return list;
        foreach (var (ev, entries) in hooks)
            foreach (var entry in entries?.AsArray() ?? new JsonArray())
                foreach (var h in entry?["hooks"]?.AsArray() ?? new JsonArray())
                    if (h?["command"]?.GetValue<string>() is { } cmd)
                        list.Add((ev, entry?["matcher"]?.GetValue<string>(), cmd));
        return list;
    }

    /// <summary>The settings entry that runs this hook (same command, or same script) under the event, if any.</summary>
    public static JsonNode? Find(JsonObject settings, string ev, string command)
    {
        var key = ScriptKey(command);
        foreach (var entry in settings["hooks"]?[ev]?.AsArray() ?? new JsonArray())
            foreach (var h in entry?["hooks"]?.AsArray() ?? new JsonArray())
                if (h?["command"]?.GetValue<string>() is { } c && (c == command || ScriptKey(c) == key)) return entry;
        return null;
    }

    /// <summary>Adds what the kit wants and the repo lacks, in place. Returns one line per addition.</summary>
    public static List<string> Merge(JsonObject target, JsonObject kit)
    {
        var added = new List<string>();
        if (kit["hooks"] is JsonObject kitHooks)
        {
            if (target["hooks"] is not JsonObject) target["hooks"] = new JsonObject();
            var hooks = (JsonObject)target["hooks"]!;
            foreach (var (ev, entries) in kitHooks)
                foreach (var entry in entries?.AsArray() ?? new JsonArray())
                {
                    var all = entry?["hooks"]?.AsArray() ?? new JsonArray();
                    var missing = all.Where(h => h?["command"]?.GetValue<string>() is { } c && Find(target, ev, c) == null).ToList();
                    if (missing.Count == 0) continue;
                    var clone = entry!.DeepClone().AsObject();
                    var keep = new JsonArray();
                    foreach (var h in missing) keep.Add(h!.DeepClone());
                    clone["hooks"] = keep;
                    if (hooks[ev] is not JsonArray) hooks[ev] = new JsonArray();
                    hooks[ev]!.AsArray().Add(clone);
                    foreach (var h in missing) added.Add($"{ev} {ScriptKey(h!["command"]!.GetValue<string>())}");
                }
        }
        foreach (var (k, v) in kit)
            if (k != "hooks" && target[k] == null) { target[k] = v?.DeepClone(); added.Add(k); }
        return added;
    }
}
