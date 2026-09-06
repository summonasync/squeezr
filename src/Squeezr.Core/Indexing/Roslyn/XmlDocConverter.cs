using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Squeezr.Core.Indexing.Roslyn;

public static class XmlDocConverter
{
    public static string? FromSymbol(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        return string.IsNullOrWhiteSpace(xml) ? null : ToPlainText(xml);
    }

    public static string? FromTrivia(DocumentationCommentTriviaSyntax trivia)
    {
        var xml = string.Concat(trivia.Content.Select(c => c.ToString()));
        return string.IsNullOrWhiteSpace(xml) ? null : ToPlainText($"<root>{xml}</root>");
    }

    private static string? ToPlainText(string xml)
    {
        try
        {
            var element = XElement.Parse(xml, LoadOptions.None);
            var text = Render(element).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string Render(XElement element)
    {
        var parts = new List<string>();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    parts.Add(text.Value);
                    break;
                case XElement child:
                    if (child.Name.LocalName is "paramref" or "typeparamref")
                    {
                        parts.Add(child.Attribute("name")?.Value ?? string.Empty);
                    }
                    else if (child.Name.LocalName is "see" or "seealso")
                    {
                        parts.Add(child.Attribute("cref")?.Value ?? child.Value);
                    }
                    else if (child.Name.LocalName is "para" or "br")
                    {
                        parts.Add("\n" + Render(child).Trim() + "\n");
                    }
                    else
                    {
                        parts.Add(Render(child));
                    }

                    break;
            }
        }

        return string.Concat(parts);
    }
}
