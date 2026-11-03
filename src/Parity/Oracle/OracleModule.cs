using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Parity.Lint;
using Parity.Model;

namespace Parity.Oracle;

/// <summary>
/// <c>verified/&lt;scenario&gt;.json</c>, written only by <c>parity oracle verify</c> after the replay's tests passed and the diff was
/// empty: the recording, the rule set and the replay it compared, by seal and hash. A scenario's <c>lastVerified</c> is backed by it.
/// </summary>
public sealed record Verification(string Scenario, int ScenarioVersion, string Date, string RecordingSeal, int RulesVersion, string RulesHash, string ReplaySeal, int Calls, string Tests);

/// <summary>One run of the replay command: the scenario, where its capture goes, and the module.</summary>
public sealed record ReplayRun(string Scenario, string Capture, string ModuleDir);

/// <summary>Runs the new client's tests for one scenario; the exit code is the test result.</summary>
public interface IReplayRunner
{
    int Run(ReplayRun run);
}

/// <summary>
/// The configured <c>oracle.replay</c> command, run by <c>sh -c</c> (Git Bash on Windows, as the hooks) from the current directory,
/// with <c>PARITY_SCENARIO</c>, <c>PARITY_CAPTURE</c> and <c>PARITY_MODULE_DIR</c> set and <c>{scenario}</c>, <c>{capture}</c> and
/// <c>{module}</c> replaced by the same values, single-quoted. Its output goes to <paramref name="log"/>.
/// </summary>
public sealed class ShellReplayRunner(string command, TextWriter log) : IReplayRunner
{
    public int Run(ReplayRun run)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var cmd = command.Replace("{scenario}", Q(run.Scenario)).Replace("{capture}", Q(run.Capture)).Replace("{module}", Q(run.ModuleDir));
        var psi = new ProcessStartInfo("sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(cmd);
        psi.Environment["PARITY_SCENARIO"] = run.Scenario;
        psi.Environment["PARITY_CAPTURE"] = run.Capture;
        psi.Environment["PARITY_MODULE_DIR"] = run.ModuleDir;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException($"cannot start sh to run oracle.replay: {ex.Message}; on Windows put Git Bash's sh on PATH"); }
        using (p)
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            log.Write(o);
            log.Write(err.Result);
            return p.ExitCode;
        }
    }
}

public sealed record VerifyResult(bool Verified, int ExitCode, string Text);

/// <summary>
/// The oracle layer's commands (<c>docs/oracle.md</c>): export a recording, diff a replay against it, and verify a scenario, the one
/// way a recorded scenario gets a <c>lastVerified</c> date.
/// </summary>
public static class OracleModule
{
    /// <summary>The scenario's current version; refuses a scenario the registry does not have.</summary>
    public static int ScenarioVersion(string moduleDir, string scenario)
    {
        Recording.Slug(scenario);
        var versions = Registry.ScenarioVersions(moduleDir)
            ?? throw new InvalidOperationException($"no readable {Recording.Rel(Path.Combine(Registry.DirOf(moduleDir), Registry.ScenariosFile))}; the map phase writes the scenarios a recording belongs to");
        return versions.TryGetValue(scenario, out var v) ? v
            : throw new InvalidOperationException($"{scenario} is not in {Recording.Rel(Path.Combine(Registry.DirOf(moduleDir), Registry.ScenariosFile))}; add the scenario in the map phase first");
    }

    /// <summary>The phase file next to the module folder: <c>legacy/.phase</c>.</summary>
    public static string? Phase(string moduleDir)
    {
        var f = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(moduleDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? ".", ".phase");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
    }

    /// <summary><c>parity oracle export</c>: a recording, only in the record phase (or where no phase file exists).</summary>
    public static (string Dir, RecordingFile File) Export(ExportRequest q)
    {
        var phase = Phase(q.ModuleDir);
        if (!string.IsNullOrEmpty(phase) && phase != "record")
            throw new InvalidOperationException($"export refused in the '{phase}' phase: recordings are written only in the record phase, with the person who ran the scenario; set legacy/.phase to record");
        return Recording.Export(q with { Kind = "recording" });
    }

    /// <summary>The recording of a scenario, refused when its seal is broken.</summary>
    public static (string Dir, RecordingFile File) SealedRecording(string moduleDir, string scenario)
    {
        var dir = Recording.RecordingDir(moduleDir, scenario);
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"{scenario} has no recording at {Recording.Rel(dir)}; a person records it once in the record phase (parity oracle export)");
        var rec = Recording.Load(dir);
        var tampered = Recording.Tampered(dir, rec);
        if (tampered.Count > 0)
            throw new InvalidOperationException($"the recording of {scenario} no longer matches its seal ({string.Join("; ", tampered)}); a recording is read-only after export. Restore it from git, or record the scenario again");
        if (rec.Kind != "recording" || rec.Scenario != scenario) throw new InvalidOperationException($"{Recording.Rel(dir)} holds a {rec.Kind} of {rec.Scenario}, not the recording of {scenario}");
        return (dir, rec);
    }

    /// <summary>The rule set, refused while any rule is invalid or undecided.</summary>
    public static NormSet Rules(string moduleDir)
    {
        var (set, findings) = Normalization.Load(moduleDir, Registry.Decisions(moduleDir));
        if (findings.Count > 0)
            throw new InvalidOperationException($"normalization rules refused ({findings.Count} finding(s)); the diff runs only on rules a person decided:\n" + string.Join("\n", findings.Select(x => x.ToString())));
        return set;
    }

    /// <summary>Imports a capture (host log rows of one new-client run) as the scenario's replay, through the same redaction as a recording.</summary>
    public static (string Dir, RecordingFile File) Import(string moduleDir, string scenario, string capture, OracleConfig cfg, string? redactKey, DateOnly today) =>
        Recording.Export(new ExportRequest(moduleDir, scenario, "replay", new() { capture }, new(), null, null, null, null, null, true, cfg, redactKey, today));

    /// <summary><c>parity oracle diff</c>: the replay (a fresh capture when given, else the last replay) against the recording. Never marks anything verified.</summary>
    public static DiffResult Diff(string moduleDir, string scenario, string? capture, OracleConfig cfg, string? redactKey, DateOnly today)
    {
        var (recDir, rec) = SealedRecording(moduleDir, scenario);
        var rules = Rules(moduleDir);
        var (repDir, rep) = capture != null ? Import(moduleDir, scenario, capture, cfg, redactKey, today) : LoadReplay(moduleDir, scenario);
        return OracleDiff.Compare(recDir, rec, repDir, rep, rules);
    }

    private static (string, RecordingFile) LoadReplay(string moduleDir, string scenario)
    {
        var dir = Recording.ReplayDir(moduleDir, scenario);
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"{scenario} has no replay at {Recording.Rel(dir)}; pass --capture <log rows of a new-client run> or run parity oracle verify");
        return (dir, Recording.Load(dir));
    }

    /// <summary>
    /// <c>parity oracle verify</c>: runs the replay command (the new client's tests for the scenario, which leave the capture), refuses
    /// a failed run or a run that left no capture, imports the capture, diffs it, and only on an empty diff writes the verification
    /// and the scenario's <c>lastVerified</c>. The capture lives in a temporary file and is deleted: only its redacted import stays.
    /// </summary>
    public static VerifyResult Verify(string moduleDir, string scenario, OracleConfig cfg, IReplayRunner runner, string? redactKey, DateOnly today)
    {
        var version = ScenarioVersion(moduleDir, scenario);
        var (recDir, rec) = SealedRecording(moduleDir, scenario);
        var rules = Rules(moduleDir);
        var capture = Path.Combine(Path.GetTempPath(), $"parity-capture-{Guid.NewGuid():N}.jsonl");
        try
        {
            var code = runner.Run(new ReplayRun(scenario, capture, moduleDir));
            if (code != 0) return new VerifyResult(false, 1, $"{scenario}: not verified: the replay command failed (exit {code}), so the tests are not green\n");
            if (!File.Exists(capture) || new FileInfo(capture).Length == 0)
                throw new InvalidOperationException($"the replay command passed but left no capture at $PARITY_CAPTURE; it must write the host log rows of the run there (docs/oracle.md, The replay command)");
            var (repDir, rep) = Import(moduleDir, scenario, capture, cfg, redactKey, today);
            var diff = OracleDiff.Compare(recDir, rec, repDir, rep, rules);
            var report = OracleDiff.Render(diff, Where(moduleDir));
            if (diff.Differences.Count > 0) return new VerifyResult(false, 1, report + $"\n{scenario}: not verified: {diff.Differences.Count} difference(s)\n");
            var date = today.ToString("yyyy-MM-dd");
            var v = new Verification(scenario, version, date, rec.Seal, rules.Version, rules.Hash, rep.Seal, rec.Calls.Count, "replay command exit 0");
            var vf = Recording.VerifiedFile(moduleDir, scenario);
            Directory.CreateDirectory(Path.GetDirectoryName(vf)!);
            File.WriteAllText(vf, JsonSerializer.Serialize(v, Json.Options).Replace("\r\n", "\n") + "\n");
            SetLastVerified(Path.Combine(Registry.DirOf(moduleDir), Registry.ScenariosFile), scenario, date);
            return new VerifyResult(true, 0, report + $"\n{scenario}: verified {date}: {rec.Calls.Count} call(s), 0 differences, tests green\n");
        }
        finally { if (File.Exists(capture)) File.Delete(capture); }
    }

    /// <summary>Where the legacy client makes a wire call: the svc: ids that map to it, each with its callers from module.json.</summary>
    public static Func<string, List<string>> Where(string moduleDir)
    {
        var wire = Registry.Wire(moduleDir);
        var modulePath = Path.Combine(moduleDir, "module.json");
        var deps = File.Exists(modulePath)
            ? (JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(modulePath), Json.Options)?.Dependencies ?? new()).ToDictionary(d => d.Id, StringComparer.Ordinal)
            : new Dictionary<string, DependencyEntry>(StringComparer.Ordinal);
        return call => wire.TryGetValue(call, out var svcs)
            ? svcs.Select(s => deps.TryGetValue(s, out var d) && d.Callers.Count > 0
                ? $"{s}, called from " + string.Join(", ", d.Callers.Select(c => $"{c.Via} ({string.Join(", ", c.Args)})"))
                : s).ToList()
            : new List<string>();
    }

    /// <summary>
    /// Sets one scenario's <c>lastVerified</c> in scenarios.json by editing the text in place, so the file keeps its layout: the
    /// value's token is replaced, or the field is added after the scenario's last field.
    /// </summary>
    public static void SetLastVerified(string scenariosPath, string scenario, string date)
    {
        var text = File.ReadAllText(scenariosPath);
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        // find the object at depth 3 (root > scenarios array > scenario) whose id is the scenario
        long objStart = -1, valueStart = -1, valueEnd = -1, lastValueEnd = -1, found = -1;
        string? prop = null;
        var hit = false;
        while (!hit && reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject when reader.CurrentDepth == 2:
                    objStart = reader.TokenStartIndex; valueStart = valueEnd = lastValueEnd = -1; prop = null; break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 3:
                    prop = reader.GetString(); break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 2:
                    hit = found >= 0 && found == objStart; break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray when reader.CurrentDepth == 3:
                    reader.Skip(); lastValueEnd = reader.TokenStartIndex + 1; break;
                default:
                    if (reader.CurrentDepth != 3) break;
                    lastValueEnd = reader.TokenStartIndex + reader.ValueSpan.Length + (reader.TokenType == JsonTokenType.String ? 2 : 0);
                    if (prop == "id" && reader.TokenType == JsonTokenType.String && reader.GetString() == scenario) found = objStart;
                    if (prop == "lastVerified") { valueStart = reader.TokenStartIndex; valueEnd = lastValueEnd; }
                    break;
            }
        }
        if (!hit) throw new InvalidOperationException($"{scenario} is not in {Recording.Rel(scenariosPath)}");
        string Str(long from, long to) => Encoding.UTF8.GetString(bytes, (int)from, (int)(to - from));
        string result;
        if (valueStart >= 0)
            result = Str(0, valueStart) + $"\"{date}\"" + Str(valueEnd, bytes.Length);
        else
        {
            var before = Str(0, lastValueEnd);
            var lineStart = before.LastIndexOf('\n') + 1;
            var indent = new string(before[lineStart..].TakeWhile(char.IsWhiteSpace).ToArray());
            var inline = !Str(objStart, lastValueEnd).Contains('\n');
            result = before + (inline ? $", \"lastVerified\": \"{date}\"" : $",\n{indent}\"lastVerified\": \"{date}\"") + Str(lastValueEnd, bytes.Length);
        }
        File.WriteAllText(scenariosPath, result);
    }

    /// <summary>
    /// The registry check's oracle part: every recording sealed and for a scenario the registry has at its version
    /// (RECORDING-TAMPERED, SCENARIO-UNKNOWN, RECORDING-VERSION), and a recorded scenario's lastVerified backed by a current
    /// verification (SCENARIO-VERIFY-UNBACKED).
    /// </summary>
    public static List<Finding> Check(string moduleDir, List<(Scenario S, int Line)> scenarios, string scnFile, NormSet rules)
    {
        var f = new List<Finding>();
        var root = Path.Combine(moduleDir, "recordings");
        var recorded = new Dictionary<string, RecordingFile>(StringComparer.Ordinal);
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var file = Recording.Rel(Path.Combine(dir, Recording.IndexFile));
                RecordingFile rec;
                try { rec = Recording.Load(dir); }
                catch (InvalidOperationException ex) { f.Add(new Finding(file, 0, "RECORDING-INVALID", "error", ex.Message)); continue; }
                foreach (var t in Recording.Tampered(dir, rec))
                    f.Add(new Finding(file, 0, "RECORDING-TAMPERED", "error", $"{t}; a recording is read-only after export: restore it from git or record the scenario again"));
                if ("scn:" + Path.GetFileName(dir) != rec.Scenario) f.Add(new Finding(file, 0, "RECORDING-INVALID", "error", $"the folder {Path.GetFileName(dir)} holds a recording of {rec.Scenario}"));
                var s = scenarios.FirstOrDefault(x => x.S.Id == rec.Scenario);
                if (s.S == null) { f.Add(new Finding(file, 0, "SCENARIO-UNKNOWN", "error", $"records {rec.Scenario}, which the registry lacks")); continue; }
                if (s.S.Version != rec.ScenarioVersion)
                    f.Add(new Finding(file, 0, "RECORDING-VERSION", "error", $"records {rec.Scenario}@{rec.ScenarioVersion}; the scenario is at version {s.S.Version}, so its meaning changed since: record it again"));
                recorded[rec.Scenario] = rec;
            }
        foreach (var (s, line) in scenarios)
        {
            if (s.LastVerified == null || !recorded.TryGetValue(s.Id, out var rec)) continue;
            var vf = Recording.VerifiedFile(moduleDir, s.Id);
            Verification? v = null;
            try { if (File.Exists(vf)) v = JsonSerializer.Deserialize<Verification>(File.ReadAllText(vf), Json.Options); }
            catch (JsonException) { }
            var why = v == null ? "no verification exists"
                : v.Date != s.LastVerified ? $"the last verification is dated {v.Date}"
                : v.RecordingSeal != rec.Seal ? "the recording changed since"
                : v.RulesHash != rules.Hash ? "the normalization rules changed since"
                : v.ScenarioVersion != s.Version ? "the scenario's version changed since"
                : null;
            if (why != null)
                f.Add(new Finding(scnFile, line, "SCENARIO-VERIFY-UNBACKED", "error",
                    $"{s.Id} is recorded, so only parity oracle verify sets its lastVerified, and {s.LastVerified} is not backed: {why}. Run parity oracle verify, or remove lastVerified"));
        }
        return f;
    }
}
