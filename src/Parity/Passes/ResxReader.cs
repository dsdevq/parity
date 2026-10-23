using System.Xml.Linq;

namespace Parity.Passes;

public static class ResxReader
{
    public static Dictionary<string, string> Read(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument doc;
        try { doc = XDocument.Load(path); } catch { return dict; }
        foreach (var d in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
        {
            var name = d.Attribute("name")?.Value;
            var value = d.Element("value")?.Value;
            if (name != null && value != null && d.Attribute("type") == null && d.Attribute("mimetype") == null)
                dict[name] = value;
        }
        return dict;
    }
}
