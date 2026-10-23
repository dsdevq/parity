using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Parity.Tests;

/// <summary>
/// A temporary consuming repo: a git repo holding a copy of fixtures/SampleLegacy (so the csproj can be detected),
/// plus what a repo that already has its own agent setup looks like - a settings.json with a foreign hook and a
/// skill folder of its own. Runs the parity CLI as a child process, the way a user does.
/// </summary>
internal sealed class ConsumingRepo : IDisposable
{
    public const string ForeignCommand = "node tools/house-hook.js";
    public string RepoRoot { get; }
    public string Dir { get; }
    public string Csproj => Path.Combine(Dir, "LegacyApp", "SampleLegacy", "SampleLegacy.csproj");
    string Base { get; }

    public ConsumingRepo(string repoRoot, bool foreignSetup = true, bool git = true)
    {
        RepoRoot = repoRoot;
        Base = Path.Combine(Path.GetTempPath(), "parity-init-" + Guid.NewGuid().ToString("N")[..8]);
        Dir = Path.Combine(Base, "repo");
        Directory.CreateDirectory(Dir);
        CopyTree(Path.Combine(repoRoot, "fixtures", "SampleLegacy"), Path.Combine(Dir, "LegacyApp", "SampleLegacy"));
        if (git) Git("init", "-q");
        if (!foreignSetup) return;
        Directory.CreateDirectory(Path.Combine(Dir, ".claude", "skills", "house-style"));
        File.WriteAllText(Path.Combine(Dir, ".claude", "skills", "house-style", "SKILL.md"), "---\nname: house-style\ndescription: the repo's own skill\n---\n\nKeep it tidy.\n");
        File.WriteAllText(SettingsPath, """
        {
          "permissions": { "allow": ["Bash(npm test)"] },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "node tools/house-hook.js" } ] }
            ],
            "Stop": [
              { "hooks": [ { "type": "command", "command": "node tools/house-stop.js" } ] }
            ]
          }
        }
        """.Replace("\r\n", "\n"));
    }

    public string SettingsPath => Path.Combine(Dir, ".claude", "settings.json");
    public string Path_(string rel) => Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
    public string Read(string rel) => File.ReadAllText(Path_(rel));
    public bool Exists(string rel) => File.Exists(Path_(rel)) || Directory.Exists(Path_(rel));
    public JsonNode Settings() => JsonNode.Parse(File.ReadAllText(SettingsPath))!;

    /// <summary>Every file under <paramref name="rel"/>, as repo-relative forward-slash paths.</summary>
    public List<string> Files(string rel) =>
        Directory.Exists(Path_(rel))
            ? Directory.EnumerateFiles(Path_(rel), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new();

    public (int Code, string Out, string Err) Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Exec(psi);
    }

    /// <summary>The parity CLI, from the build under test. <paramref name="env"/> entries with a null value are removed.</summary>
    public (int Code, string Out, string Err) Parity(string[] args, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { WorkingDirectory = Dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "parity.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("PARITY_BIN"); psi.Environment.Remove("PARITY_LEGACY_ROOT"); psi.Environment.Remove("PARITY_AGENT");
        if (env != null) foreach (var (k, v) in env) { if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        return Exec(psi);
    }

    public (int Code, string Out, string Err) Parity(params string[] args) => Parity(args, null);

    /// <summary>The legacy project's output folder, as its csproj spells it (bin\Debug\).</summary>
    public string OutputDir => Path.Combine(Dir, "LegacyApp", "SampleLegacy", "bin", "Debug");

    /// <summary>A tiny managed assembly, so a file the tool loads as a reference is a real one.</summary>
    public static void EmitAssembly(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText("public class Marker { }") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var r = c.Emit(path);
        if (!r.Success) throw new InvalidOperationException(string.Join("; ", r.Diagnostics));
    }

    /// <summary>Builds the legacy project's output: the project's own assembly in its OutputPath.</summary>
    public void Build() => EmitAssembly(Path.Combine(OutputDir, "SampleLegacy.exe"), "SampleLegacy");

    /// <summary>A reference-assemblies folder of <paramref name="count"/> real assemblies, named r000.dll ...</summary>
    public string References(int count)
    {
        var dir = Path.Combine(Base, "refs");
        EmitAssembly(Path.Combine(dir, "r000.dll"), "r000");
        for (var i = 1; i < count; i++) File.Copy(Path.Combine(dir, "r000.dll"), Path.Combine(dir, $"r{i:000}.dll"), true);
        return dir.Replace('\\', '/');
    }

    /// <summary>Edits legacy/extract.config.json as JSON (the file stays strict JSON).</summary>
    public void EditConfig(Action<JsonObject> edit)
    {
        var path = Path_("legacy/extract.config.json");
        var cfg = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(cfg);
        File.WriteAllText(path, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// A PATH holding only the named tools (empty files: the checks look tools up, they do not run them) plus a git wrapper,
    /// so a doctor run sees exactly the tools a test hands it. Not for Windows.
    /// </summary>
    public string Path_Only(params string[] tools)
    {
        var bin = Path.Combine(Base, "fakebin-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(bin);
        foreach (var t in tools) File.WriteAllText(Path.Combine(bin, t), "");
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "git")).FirstOrDefault(File.Exists);
        if (git != null) File.WriteAllText(Path.Combine(bin, "git"), $"#!/bin/sh\nexec '{git}' \"$@\"\n");
        if (git != null && !OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(bin, "git"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    /// <summary>The doctor's lines: status, name, detail. A name is followed by at least one space.</summary>
    public (int Code, List<(string Status, string Line)> Lines, string Err) Doctor(IDictionary<string, string?>? env = null)
    {
        var r = Parity(new[] { "doctor" }, env);
        return (r.Code, r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (l[..5].Trim(), l[6..])).ToList(), r.Err);
    }

    static (int Code, string Out, string Err) Exec(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, err.Result);
    }

    static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    public void Dispose() { try { Directory.Delete(Base, true); } catch { } }
}
