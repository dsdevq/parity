using System.Text.RegularExpressions;

namespace Parity.Tools;

/// <summary>Identifiers to $id, literals to $lit; structure words (this, new, typeof) kept.</summary>
public static class Shape
{
    public static string Of(string s)
    {
        var shape = Regex.Replace(s, "\"(?:[^\"\\\\]|\\\\.)*\"", "$lit");
        shape = Regex.Replace(shape, @"\b\d+(\.\d+)?[dDfFmM]?\b", "$lit");
        shape = Regex.Replace(shape, @"\b(true|false|null)\b", "$lit");
        shape = Regex.Replace(shape, @"(?<![\w$])(?!this\b|new\b|typeof\b)[A-Za-z_]\w*", "$id");
        return Regex.Replace(shape, @"\s+", " ").Trim();
    }
}
