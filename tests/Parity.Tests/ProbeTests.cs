using Parity.Passes;
using Parity.Project;
using Xunit;

namespace Parity.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void csproj_reader_takes_the_explicit_file_list_and_defines()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Parity.sln"))) dir = Path.GetDirectoryName(dir);
        var p = CsprojReader.Read(Path.Combine(dir!, "fixtures", "SampleLegacy", "SampleLegacy.csproj"));
        Assert.Equal(28, p.CompileFiles.Count);
        Assert.Equal(3, p.ResxFiles.Count); // a form resx, and Captions.resx with its fr culture
        Assert.Equal(5, p.References.Count);
        Assert.Single(p.ProjectReferences);
        Assert.EndsWith(Path.Combine("bin", "Debug") + Path.DirectorySeparatorChar, p.OutputDir!);
        Assert.Contains("LEGACY_FLAG", p.Defines);
        Assert.Equal("7.3", p.LangVersion);
        var idx = FileIndex.Build(p.CompileFiles);
        Assert.Equal(3, idx.FilesFor("PolicyWizardStep3").Count);
        Assert.Single(idx.FilesFor("ServiceLocator"));
    }

    [Fact]
    public void references_can_be_derived_from_the_csproj_items()
    {
        // issue #7: no solution-wide bin globs; resolve the csproj's own Reference/ProjectReference names in referenceDirs
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Parity.sln"))) dir = Path.GetDirectoryName(dir);
        var stubsBin = Path.Combine(dir!, "fixtures", "WinFormsStubs", "bin", "Debug", "net10.0");
        var tmp = Path.Combine(Path.GetTempPath(), "parity-refs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "A.csproj"),
            "<Project ToolsVersion=\"4.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <PropertyGroup><AssemblyName>A</AssemblyName><OutputPath>bin\\Debug\\</OutputPath></PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <Reference Include=\"WinFormsStubs\" />\n" +
            "    <Reference Include=\"DevExpress.Missing.v1, Version=1.0.0.0\" />\n" +
            "    <Reference Include=\"System.Data\" />\n" +
            "    <ProjectReference Include=\"..\\B\\B.csproj\" />\n" +
            "  </ItemGroup>\n" +
            "  <ItemGroup><Compile Include=\"A.cs\" /></ItemGroup>\n" +
            "</Project>\n");
        File.WriteAllText(Path.Combine(tmp, "A.cs"), "class A { }");
        var project = CsprojReader.Read(Path.Combine(tmp, "A.csproj"));
        var cfg = new Parity.Model.ExtractConfig { ReferencesFromProject = true, ReferenceDirs = new List<string> { stubsBin }, IncludeRuntimeReferences = false, BaseDir = tmp };
        var diags = new List<Parity.Model.Diagnostic>();
        var refs = CompilationBuilder.BuildReferences(project, cfg, diags);
        Assert.Single(refs);
        Assert.EndsWith("WinFormsStubs.dll", refs[0].Display!);
        Assert.Equal(new[] { "REF-MISSING", "REF-MISSING" }, diags.Select(d => d.Code));
        Assert.Contains(diags, d => d.Message.Contains("DevExpress.Missing.v1"));
        Assert.Contains(diags, d => d.Message.Contains("ProjectReference 'B'"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void canonical_form_ignores_names_and_literals()
    {
        var a = Duplicates.Canonical("void A() { if (x > 1) y = \"a\"; }");
        var b = Duplicates.Canonical("void B() { if (q > 99) z = \"zz\"; }");
        Assert.Equal(a, b);
        Assert.Equal(Duplicates.Hash(a), Duplicates.Hash(b));
    }

    [Theory]
    [InlineData("PolicyWizard*", "PolicyWizardStep3", true)]
    [InlineData("PolicyWizard*", "ClientSearchForm", false)]
    [InlineData("*Search*", "ClaimClientSearchForm", true)]
    [InlineData("*", "Anything", true)]
    public void form_glob(string pattern, string name, bool expected) => Assert.Equal(expected, Extractor.Glob(pattern)(name));
}
