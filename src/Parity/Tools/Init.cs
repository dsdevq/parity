using System.Text;
using System.Text.Json.Nodes;

namespace Parity.Tools;

public sealed record InitOptions(string RepoDir, bool Merge, string? Project, string? Module, string? PhaseDriver);

/// <param name="Action">create, update, merge, unchanged, skip, or would-update / would-merge when a repo-owned or changed file is left for --merge.</param>
public sealed record InitLine(string Action, string Path, string? Note = null)
{
    public bool Pending => Action.StartsWith("would-", StringComparison.Ordinal);
    public override string ToString() => $"{Action,-13}{Path}" + (Note != null ? $"  ({Note})" : "");
}

public sealed record InitResult(List<InitLine> Lines, List<string> Notes)
{
    public bool Pending => Lines.Any(l => l.Pending);
}

/// <summary>
/// <c>parity init</c>: lays the adopt kit out in a consuming repo. What goes where is read from the kit (adopt/kit.json), not
/// written here. Files the tool owns are created when missing; when they differ they are reported and rewritten only under
/// <c>--merge</c>. Files the repo owns (config, control categories, .phase) are created once and never touched again.
/// The repo's own settings.json, skills and hooks are never overwritten: settings are merged, foreign skills are left alone.
/// </summary>
public static class Init
{
    public static InitResult Run(Kit kit, InitOptions o)
    {
        var lines = new List<InitLine>();
        var notes = new List<string>();
        var repo = Path.GetFullPath(o.RepoDir);
        string Abs(string rel) => Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
        var m = kit.Manifest;

        // 1. files the tool owns
        foreach (var rule in m.Install)
            foreach (var (source, target) in kit.Expand(rule))
                Own(lines, Abs(target), target, kit.Files[source], o.Merge);

        // 2. files the repo owns: created once
        foreach (var rule in m.Create)
            foreach (var (source, target) in kit.Expand(rule))
                Once(lines, Abs(target), target, kit.Files[source]);

        // 3. the config skeleton
        var cfgPath = Abs(m.Config.To);
        if (File.Exists(cfgPath)) lines.Add(new InitLine("skip", m.Config.To, "yours"));
        else
        {
            var built = ConfigSkeleton.Build(kit.Text(m.Config.From), repo, Path.GetDirectoryName(cfgPath)!, o.Project);
            WriteNew(cfgPath, Encoding.UTF8.GetBytes(built.Text));
            lines.Add(new InitLine("create", m.Config.To, built.Project != null ? "from the csproj" : "placeholders: edit project"));
            notes.AddRange(built.Notes);
        }

        // 4. .phase
        var phaseFile = Abs(m.PhaseFile);
        if (File.Exists(phaseFile)) lines.Add(new InitLine("skip", m.PhaseFile, "yours"));
        else
        {
            WriteNew(phaseFile, Encoding.UTF8.GetBytes((m.Phases.FirstOrDefault()?.Name ?? "extract") + "\n"));
            lines.Add(new InitLine("create", m.PhaseFile));
        }

        // 5. settings
        Settings(kit, lines, Abs(m.Settings.To), m.Settings.To, o.Merge);

        // 6. a module's folders
        if (!string.IsNullOrEmpty(o.Module))
        {
            var root = Path.GetDirectoryName(m.Config.To)?.Replace('\\', '/') ?? "";
            foreach (var sub in new[] { "fixtures", "spec" })
            {
                var rel = (root.Length > 0 ? root + "/" : "") + $"{o.Module}/{sub}/.gitkeep";
                Once(lines, Abs(rel), rel, Array.Empty<byte>());
            }
        }

        // 7. the phase driver
        if (o.PhaseDriver != null)
        {
            var kind = o.PhaseDriver.Length > 0 ? o.PhaseDriver : OperatingSystem.IsWindows() ? "cmd" : "sh";
            if (kind is not ("sh" or "cmd")) throw new ArgumentException("--phase-driver takes sh or cmd (or nothing, for this platform)");
            var sh = PhaseDriver.ScriptPath(m, "sh");
            Own(lines, Abs(sh), sh, Encoding.UTF8.GetBytes(PhaseDriver.Sh(m)), o.Merge);
            if (kind == "cmd")
            {
                var cmd = PhaseDriver.ScriptPath(m, "cmd");
                Own(lines, Abs(cmd), cmd, Encoding.UTF8.GetBytes(PhaseDriver.Cmd(m)), o.Merge);
            }
        }

        if (lines.Any(l => l.Pending))
            notes.Add("some files differ from the kit or need merging: parity init --merge applies them (your own files, config and skills stay as they are)");
        return new InitResult(lines, notes);
    }

    static string Norm(byte[] b) => Encoding.UTF8.GetString(b).Replace("\r\n", "\n");

    static void WriteNew(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static void Own(List<InitLine> lines, string path, string rel, byte[] want, bool merge)
    {
        if (!File.Exists(path)) { WriteNew(path, want); lines.Add(new InitLine("create", rel)); return; }
        if (Norm(File.ReadAllBytes(path)) == Norm(want)) { lines.Add(new InitLine("unchanged", rel)); return; }
        if (!merge) { lines.Add(new InitLine("would-update", rel, "differs from the kit")); return; }
        File.WriteAllBytes(path, want);
        lines.Add(new InitLine("update", rel));
    }

    static void Once(List<InitLine> lines, string path, string rel, byte[] want)
    {
        if (File.Exists(path)) { lines.Add(new InitLine("skip", rel, "yours")); return; }
        WriteNew(path, want);
        lines.Add(new InitLine("create", rel));
    }

    static void Settings(Kit kit, List<InitLine> lines, string path, string rel, bool merge)
    {
        var want = SettingsMerge.Parse(kit.Text(kit.Manifest.Settings.From), kit.Manifest.Settings.From);
        if (!File.Exists(path)) { WriteNew(path, Encoding.UTF8.GetBytes(SettingsMerge.Serialize(want))); lines.Add(new InitLine("create", rel)); return; }

        var have = SettingsMerge.Parse(File.ReadAllText(path), rel);
        var added = SettingsMerge.Merge(have, want);
        if (added.Count == 0) { lines.Add(new InitLine("unchanged", rel)); return; }
        var what = "adds " + string.Join(", ", added);
        if (!merge) { lines.Add(new InitLine("would-merge", rel, what + "; your entries stay")); return; }
        File.WriteAllText(path, SettingsMerge.Serialize(have));
        lines.Add(new InitLine("merge", rel, what));
    }
}
