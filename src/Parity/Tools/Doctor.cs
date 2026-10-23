using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parity.Model;
using Parity.Project;

namespace Parity.Tools;

public sealed record Check(string Status, string Name, string Detail)
{
    public override string ToString() => $"{Status,-5} {Name,-32} {Detail}";
}

/// <summary>
/// <c>parity doctor</c>: one line per thing that has to be true before the phases can run in this repo, each pass, warn or fail.
/// A fail is something that stops a phase (or makes a hook refuse every call); a warn is something that will cost time or
/// confusion. What the kit installs is read from adopt/kit.json, so the install checks follow the kit.
/// </summary>
public static class Doctor
{
    /// <summary>Above this many reference DLLs, loading them behind an antivirus scanner takes minutes per run.</summary>
    public const int ReferenceWarnAbove = 200;

    public static List<Check> Run(Kit kit, string repoDir, string? configArg)
    {
        var checks = new List<Check>();
        void Add(string status, string name, string detail) => checks.Add(new Check(status, name, detail));
        var m = kit.Manifest;
        string Abs(string rel) => Path.Combine(repoDir, rel.Replace('/', Path.DirectorySeparatorChar));

        // --- tools
        Add(Dotnet(out var dotnetDetail), ".net sdk", dotnetDetail);

        var bin = Environment.GetEnvironmentVariable("PARITY_BIN");
        if (!string.IsNullOrEmpty(bin))
        {
            var found = File.Exists(bin) ? bin : OnPath(bin);
            Add(found != null ? "pass" : "fail", "parity binary", found != null ? $"PARITY_BIN {found}" : $"PARITY_BIN is set to '{bin}', which is not a file or on PATH: the skills and hooks cannot run it");
        }
        else
        {
            var found = OnPath("parity");
            Add(found != null ? "pass" : "fail", "parity binary", found != null ? $"parity on PATH: {found}" : "no PARITY_BIN and no parity on PATH: the skills and the stop hook call it. Set PARITY_BIN to the binary's path");
        }

        var sh = OnPath("sh");
        Add(sh != null ? "pass" : "fail", "sh", sh != null ? sh : "no sh on PATH: the hooks are POSIX sh (Git for Windows ships one; use Git Bash)");
        var jq = OnPath("jq");
        Add(jq != null ? "pass" : "fail", "jq", jq != null ? jq : "no jq on PATH: the guard hook refuses every call in specify and implement without it (winget install jqlang.jq, scoop install jq, brew install jq, apt install jq)");

        // --- config
        var cfgPath = configArg ?? new[] { "legacy/extract.config.json", "extract.config.json" }.Select(Abs).FirstOrDefault(File.Exists) ?? Abs(m.Config.To);
        ExtractConfig? cfg = null;
        if (!File.Exists(cfgPath)) Add("fail", "config", $"{Rel(repoDir, cfgPath)} not found: parity init creates it");
        else
        {
            try { cfg = ExtractConfig.Load(cfgPath); }
            catch (Exception ex) { Add("fail", "config", $"{Rel(repoDir, cfgPath)} does not parse: {ex.Message}"); }
            if (cfg != null)
            {
                try { using var _ = JsonDocument.Parse(File.ReadAllText(cfgPath)); Add("pass", "config", Rel(repoDir, cfgPath)); }
                catch (JsonException ex) { Add("fail", "config", $"{Rel(repoDir, cfgPath)} is not strict JSON ({ex.Message}); parity reads it but the guard hook's jq does not, and it refuses every call. Remove the comments and trailing commas"); }
            }
        }

        // --- the legacy root the guard works from
        var rootVar = Environment.GetEnvironmentVariable("PARITY_LEGACY_ROOT");
        if (!string.IsNullOrEmpty(rootVar))
            Add(Directory.Exists(rootVar) ? "pass" : "fail", "legacy root", Directory.Exists(rootVar) ? $"PARITY_LEGACY_ROOT {rootVar}" : $"PARITY_LEGACY_ROOT is set to '{rootVar}', which is not a directory: the guard would lock the wrong place");
        else if (cfg != null && cfg.Project.Length > 0)
        {
            var projDir = Path.GetDirectoryName(cfg.Resolve(cfg.Project))!;
            Add(Directory.Exists(projDir) ? "pass" : "warn", "legacy root", Directory.Exists(projDir) ? $"PARITY_LEGACY_ROOT unset; the guard uses the project's folder {projDir}" : $"PARITY_LEGACY_ROOT unset and the project's folder {projDir} is not on this machine: the guard locks a path that does not exist here. Set PARITY_LEGACY_ROOT to the legacy project directory");
        }
        else Add("fail", "legacy root", "PARITY_LEGACY_ROOT is unset and the config has no project: the guard refuses every call in specify and implement");

        // --- the legacy project
        if (cfg != null) Project(cfg, checks);

        // --- what init installs
        foreach (var rule in m.Install) Installed(kit, rule, repoDir, checks);
        Hooks(kit, repoDir, checks);

        // --- phase
        var phaseFile = Abs(m.PhaseFile);
        if (!File.Exists(phaseFile)) Add("warn", "phase", $"{m.PhaseFile} missing: the guard treats a missing phase as unrestricted. parity init creates it");
        else
        {
            var phase = File.ReadAllText(phaseFile).Trim();
            var known = m.Phases.Select(p => p.Name).ToList();
            Add(known.Contains(phase) ? "pass" : "fail", "phase", known.Contains(phase) ? phase : $"'{phase}' in {m.PhaseFile} is not one of {string.Join(", ", known)}");
        }

        GitIgnore(kit, repoDir, checks);
        return checks;
    }

    static string Rel(string repo, string path) => Path.GetRelativePath(repo, path).Replace('\\', '/');

    static void Project(ExtractConfig cfg, List<Check> checks)
    {
        void Add(string s, string n, string d) => checks.Add(new Check(s, n, d));
        var csproj = cfg.Resolve(cfg.Project);
        if (cfg.Project.Length == 0 || !File.Exists(csproj))
        {
            Add("fail", "legacy project", cfg.Project.Length == 0 ? "project is empty in the config" : $"project not found: {csproj}");
            return;
        }
        var project = CsprojReader.Read(csproj);
        Add("pass", "legacy project", csproj.Replace('\\', '/'));

        // reference assemblies
        var (_, tfv, tf) = ConfigSkeleton.Props(csproj);
        var framework = ConfigSkeleton.FrameworkFolder(tfv, tf);
        var missingDirs = cfg.References
            .Where(r => { var p = cfg.Resolve(r); p = p.Contains('*') ? Path.GetDirectoryName(p)! : p; return !Directory.Exists(p) && !File.Exists(p); })
            .Select(r => r.Contains('*') ? r[..r.LastIndexOfAny(new[] { '/', '\\' })] : r)
            .Distinct().ToList();
        if (missingDirs.Count > 0) Add("fail", "reference assemblies", "not found: " + string.Join("; ", missingDirs.Select(d => d.Replace('\\', '/'))));
        else if (framework != null && cfg.IncludeRuntimeReferences) Add("warn", "reference assemblies", $"the project targets .NET Framework {framework} and includeRuntimeReferences is true: System types become ambiguous with the embedded .NET references. Set it to false and list the {framework} Reference Assemblies folder in references");
        else if (framework != null && cfg.References.Count == 0) Add("fail", "reference assemblies", $"the project targets .NET Framework {framework}, includeRuntimeReferences is false and references is empty: nothing resolves System types");
        else Add("pass", "reference assemblies", framework != null ? $"{cfg.References.Count} entries for {framework}" : "the embedded .NET references");

        // output path and whether it is built
        if (project.OutputDir == null) Add("warn", "output path", "the csproj has no OutputPath: set referenceDirs and selfAssembly to where the project is built");
        else if (!Directory.Exists(project.OutputDir)) Add("fail", "output path", $"{project.OutputDir.Replace('\\', '/')} does not exist: build the legacy project once, parity reads its outputs");
        else Add("pass", "output path", project.OutputDir.Replace('\\', '/'));

        var self = cfg.SelfAssembly != null ? cfg.Resolve(cfg.SelfAssembly)
            : project.OutputDir != null ? new[] { ".dll", ".exe" }.Select(e => Path.Combine(project.OutputDir, project.AssemblyName + e)).FirstOrDefault(File.Exists)
            : null;
        if (self == null) Add("warn", "built", "no selfAssembly in the config and no OutputPath to look in: types from files outside the module will not resolve");
        else Add(File.Exists(self) ? "pass" : "fail", "built", File.Exists(self) ? Path.GetFileName(self) : $"{self.Replace('\\', '/')} not found: build the legacy project (parity never builds it)");

        // the reference count, resolved the way extract resolves it
        var diags = new List<Diagnostic>();
        int count;
        try
        {
            cfg.IncludeRuntimeReferences = false;
            count = CompilationBuilder.BuildReferences(project, cfg, diags).Count;
        }
        catch (Exception ex) { Add("fail", "references", ex.Message); return; }
        var missing = diags.Where(d => d.Code == "REF-MISSING").ToList();
        if (count > ReferenceWarnAbove)
            Add("warn", "references", $"{count} DLLs resolve (above {ReferenceWarnAbove}): behind an antivirus scanner that is minutes per extract. Narrow references and referenceDirs to the framework folders and the project's own output");
        else if (missing.Count > 0)
            Add("warn", "references", $"{count} DLLs resolve; {missing.Count} not found, e.g. {missing[0].Message}");
        else Add("pass", "references", $"{count} DLLs resolve");
    }

    static void Installed(Kit kit, KitRule rule, string repo, List<Check> checks)
    {
        var missing = new List<string>();
        var differ = new List<string>();
        var n = 0;
        foreach (var (source, target) in kit.Expand(rule))
        {
            n++;
            var path = Path.Combine(repo, target.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) missing.Add(target);
            else if (File.ReadAllText(path).Replace("\r\n", "\n") != kit.Text(source).Replace("\r\n", "\n")) differ.Add(target);
        }
        var name = rule.Name;
        if (missing.Count > 0) checks.Add(new Check("fail", name, $"missing {Few(missing)}: parity init installs {(n == 1 ? "it" : "them")}"));
        else if (differ.Count > 0) checks.Add(new Check("warn", name, $"{Few(differ)} differ{(differ.Count == 1 ? "s" : "")} from this parity's kit: parity init --merge updates {(differ.Count == 1 ? "it" : "them")}"));
        else checks.Add(new Check("pass", name, $"{n} file{(n == 1 ? "" : "s")} in {rule.To}"));
    }

    static string Few(List<string> items) => items.Count <= 3 ? string.Join(", ", items) : string.Join(", ", items.Take(3)) + $" and {items.Count - 3} more";

    static void Hooks(Kit kit, string repo, List<Check> checks)
    {
        var m = kit.Manifest;
        var settingsPath = Path.Combine(repo, m.Settings.To.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(settingsPath)) { checks.Add(new Check("fail", "hooks", $"{m.Settings.To} not found: parity init writes the hooks block")); return; }
        JsonObject settings;
        try { settings = SettingsMerge.Parse(File.ReadAllText(settingsPath), m.Settings.To); }
        catch (Exception ex) { checks.Add(new Check("fail", "hooks", ex.Message)); return; }

        var kitSettings = SettingsMerge.Parse(kit.Text(m.Settings.From), m.Settings.From);
        var installed = m.Install.SelectMany(kit.Expand).Select(t => t.Target).ToList();
        foreach (var (ev, matcher, command) in SettingsMerge.Wanted(kitSettings))
        {
            var key = SettingsMerge.ScriptKey(command);
            var label = $"hook {ev} {key}";
            var entry = SettingsMerge.Find(settings, ev, command);
            if (entry == null) { checks.Add(new Check("fail", label, $"not in {m.Settings.To}: parity init --merge adds it")); continue; }
            var script = installed.FirstOrDefault(t => Path.GetFileName(t) == key);
            if (script != null && !File.Exists(Path.Combine(repo, script.Replace('/', Path.DirectorySeparatorChar))))
            { checks.Add(new Check("fail", label, $"wired, but {script} is missing")); continue; }
            var have = entry["matcher"]?.GetValue<string>();
            if (have != matcher) checks.Add(new Check("warn", label, $"wired with matcher '{have}', the kit's is '{matcher}': a tool the kit's matcher covers may reach the guard unchecked"));
            else checks.Add(new Check("pass", label, "wired"));
        }
    }

    static void GitIgnore(Kit kit, string repo, List<Check> checks)
    {
        var inRepo = Git(repo, "rev-parse", "--is-inside-work-tree");
        if (inRepo.Code != 0) { checks.Add(new Check("warn", "git ignore", "not a git work tree (or no git): cannot tell whether legacy/ and the module folders are ignored")); return; }

        var m = kit.Manifest;
        var legacy = m.Config.To.Contains('/') ? m.Config.To[..m.Config.To.IndexOf('/')] : "";
        var paths = new List<string> { m.Config.To, m.Settings.To };
        paths.AddRange(m.Install.Where(r => r.To.StartsWith(".claude", StringComparison.Ordinal)).Select(r => r.To));
        var legacyDir = Path.Combine(repo, legacy);
        var kitDirs = m.Install.Select(r => r.To.Split('/').Skip(1).FirstOrDefault()).Where(x => x != null).ToHashSet();
        if (legacy.Length > 0 && Directory.Exists(legacyDir))
            paths.AddRange(Directory.EnumerateDirectories(legacyDir).Select(d => Path.GetFileName(d)).Where(d => !kitDirs.Contains(d)).OrderBy(d => d, StringComparer.Ordinal).Select(d => $"{legacy}/{d}"));

        var ignored = new List<string>();
        foreach (var p in paths.Distinct())
        {
            if (!File.Exists(Path.Combine(repo, p)) && !Directory.Exists(Path.Combine(repo, p))) continue;
            if (Git(repo, "check-ignore", "-q", "--no-index", p).Code == 0 && Git(repo, "ls-files", "--error-unmatch", p).Code != 0) ignored.Add(p);
        }
        checks.Add(ignored.Count > 0
            ? new Check("fail", "git ignore", $"ignored: {Few(ignored)}. The config, hooks, specs and manifests are meant to be committed; a clone would lose them")
            : new Check("pass", "git ignore", "legacy/, the module folders and .claude/ are not ignored"));
    }

    static (int Code, string Out) Git(string repo, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
        catch { return (127, ""); }
    }

    static string Dotnet(out string detail)
    {
        var dotnet = OnPath("dotnet");
        if (dotnet == null) { detail = "no dotnet on PATH: fine with a release binary; building parity from source needs the 10.x SDK"; return "warn"; }
        var psi = new ProcessStartInfo(dotnet, "--list-sdks") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            var tens = o.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("10.", StringComparison.Ordinal)).ToList();
            if (tens.Count > 0) { detail = tens[^1].Split(' ')[0]; return "pass"; }
            detail = "no 10.x SDK listed: fine with a release binary; building parity from source needs it";
            return "warn";
        }
        catch (Exception ex) { detail = $"dotnet did not run: {ex.Message}"; return "warn"; }
    }

    /// <summary>First match for a command name on PATH (name, name.exe, name.cmd, name.bat), or null.</summary>
    public static string? OnPath(string name)
    {
        if (name.Contains('/') || name.Contains('\\')) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var e in exts)
            {
                var p = Path.Combine(dir, name + e);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
