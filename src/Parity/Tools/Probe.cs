using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Parity.Passes;
using Parity.Project;

namespace Parity.Tools;

public sealed record ProbeResult(
    int CompileFiles, int Resx, string? OutputDir, int References, int ReferencesWithHintPath, int ProjectReferences,
    int MatchedFiles, int Lines, int BiggestFile, int DesignerFiles, int InitStatements, int Wirings, int ObjectCreations,
    Dictionary<string, int> Shapes, Dictionary<string, int> CreatedNamespaces, double Seconds, string Text);

/// <summary>
/// Reconnaissance without semantics: counts and the distinct statement shapes inside InitializeComponent with
/// identifiers and literals stripped. Needs no references and no build. Safe to paste into an issue.
/// Designer companions match case-insensitively (Form.Designer.cs and Form.designer.cs both belong to Form).
/// </summary>
public static class Probe
{
    public static ProbeResult Run(string csproj, string formGlob)
    {
        var sw = Stopwatch.StartNew();
        var project = CsprojReader.Read(csproj);
        var match = Extractor.Glob(formGlob);
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var newTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        int files = 0, designerFiles = 0, wiring = 0, news = 0, initStatements = 0, lines = 0, biggest = 0;
        foreach (var f in project.CompileFiles)
        {
            var baseName = Regex.Replace(Path.GetFileName(f), @"\.designer\.cs$", "", RegexOptions.IgnoreCase);
            baseName = Regex.Replace(baseName, @"\.cs$", "", RegexOptions.IgnoreCase);
            if (!match(baseName)) continue;
            var text = File.ReadAllText(f);
            files++;
            var lc = text.Count(c => c == '\n') + 1; lines += lc; if (lc > biggest) biggest = lc;
            wiring += Regex.Matches(text, @"\+=\s*new\s+[\w.]+\(|\+=\s*[\w.]+;|\+=\s*\(").Count;
            foreach (Match mm in Regex.Matches(text, @"new\s+([\w.]+)\s*\("))
            {
                var t = mm.Groups[1].Value;
                var ns = t.Contains('.') ? t[..t.IndexOf('.')] : "(unqualified)";
                newTypes[ns] = newTypes.GetValueOrDefault(ns) + 1; news++;
            }
            var init = Regex.Match(text, @"void\s+InitializeComponent\s*\(\s*\)\s*\{", RegexOptions.Multiline);
            if (!init.Success) continue;
            designerFiles++;
            var depth = 0; var body = new StringBuilder();
            for (var i = init.Index + init.Length; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '{') depth++;
                else if (c == '}') { if (depth == 0) break; depth--; }
                body.Append(c);
            }
            foreach (var raw in body.ToString().Split(';'))
            {
                var s = Regex.Replace(raw, @"//[^\n]*", "").Trim();
                if (s.Length == 0) continue;
                initStatements++;
                var shape = Shape.Of(s);
                shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine($"project: {Path.GetFileName(project.Path)}  compile files: {project.CompileFiles.Count}  resx: {project.ResxFiles.Count}");
        sb.AppendLine($"outputPath: {project.OutputDir ?? "(none in csproj)"}  references: {project.References.Count} ({project.References.Count(r => r.HintPath != null)} with HintPath)  projectReferences: {project.ProjectReferences.Count}");
        sb.AppendLine($"matched files: {files}  lines: {lines}  biggest file: {biggest} lines  designer files: {designerFiles}");
        sb.AppendLine($"InitializeComponent statements: {initStatements}  event wirings (+=) anywhere: {wiring}  object creations: {news}");
        sb.AppendLine("created-type namespaces (root): " + string.Join(", ", newTypes.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key}={k.Value}")));
        sb.AppendLine();
        sb.AppendLine("distinct InitializeComponent statement shapes (identifiers -> $id, literals -> $lit), most common first:");
        foreach (var (shape, n) in shapes.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"{n,6}  {shape}");
        return new ProbeResult(project.CompileFiles.Count, project.ResxFiles.Count, project.OutputDir, project.References.Count,
            project.References.Count(r => r.HintPath != null), project.ProjectReferences.Count, files, lines, biggest, designerFiles,
            initStatements, wiring, news, shapes, newTypes, sw.Elapsed.TotalSeconds, sb.ToString());
    }
}
