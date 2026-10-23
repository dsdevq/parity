using System.Text.RegularExpressions;

namespace Parity.Lint;

/// <summary>
/// Which rule ids a test tree really tests. An id counts only inside the title of an active test
/// declaration (<c>it</c>/<c>test</c>/<c>describe</c>, or a xunit <c>[Fact]</c>/<c>[Theory]</c> display name or method name).
/// Placeholders (<c>.todo</c>, <c>.skip</c>, <c>xit</c>, <c>Skip =</c>), comments and bare strings do not count.
/// The match is text-based, like the file filter: it does not parse the language.
/// </summary>
public sealed partial class TestDeclarations
{
    public sealed record Declaration(string File, string Form, string Title);

    public List<Declaration> Active { get; } = new();
    public List<Declaration> Inactive { get; } = new();

    // strings are matched first so a "//" inside one is not mistaken for a comment
    [GeneratedRegex(@"(?<s>""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|`(?:\\.|[^`\\])*`)|//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex StringOrComment();

    [GeneratedRegex(@"(?<![\w.$])(?<form>(?:it|test|describe)(?:\.(?:only|concurrent|sequential|skip|todo|fails|failing))*(?:\.each\s*(?:\([^)]*\)|`[^`]*`))?|xit|xtest|xdescribe|fit|fdescribe)\s*\(\s*(?<q>[""'`])(?<title>(?:\\.|(?!\k<q>).)*)\k<q>", RegexOptions.Singleline)]
    private static partial Regex JsDeclaration();

    [GeneratedRegex(@"\[\s*(?<attr>Fact|Theory)\s*(?:\((?<args>[^\]]*)\))?\s*\]\s*(?:\[[^\]]*\]\s*)*(?:\w+\s+)*?[\w<>\[\],.?]+\s+(?<name>\w+)\s*\(", RegexOptions.Singleline)]
    private static partial Regex XunitDeclaration();

    [GeneratedRegex(@"DisplayName\s*=\s*""(?<t>(?:\\.|[^""\\])*)""")]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"\bSkip\s*=")]
    private static partial Regex SkipArg();

    public static TestDeclarations Scan(IEnumerable<string> files)
    {
        var d = new TestDeclarations();
        foreach (var file in files)
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in JsDeclaration().Matches(text))
            {
                var form = Regex.Replace(m.Groups["form"].Value, @"\.each\s*(\([^)]*\)|`[^`]*`)", ".each");
                var inactive = Regex.IsMatch(form, @"(^|\.)(skip|todo)\b|^x");
                (inactive ? d.Inactive : d.Active).Add(new Declaration(file, form, m.Groups["title"].Value));
            }
            foreach (Match m in XunitDeclaration().Matches(text))
            {
                var args = m.Groups["args"].Value;
                var skipped = SkipArg().IsMatch(args);
                var form = m.Groups["attr"].Value + (skipped ? " (Skip)" : "");
                // method names cannot hold '-': R_Form_001 stands for R-Form-001
                foreach (var title in DisplayName().Matches(args).Select(x => x.Groups["t"].Value).Append(m.Groups["name"].Value.Replace('_', '-')))
                    (skipped ? d.Inactive : d.Active).Add(new Declaration(file, form, title));
            }
        }
        return d;
    }

    public static string StripComments(string text) =>
        StringOrComment().Replace(text, m => m.Groups["s"].Success ? m.Value : " ");

    public static bool Mentions(string title, string id) =>
        Regex.IsMatch(title, @"(?<![\w-])" + Regex.Escape(id) + @"(?!\w)");
}
