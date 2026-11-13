using System.Xml;
using System.Xml.Linq;

namespace Squeezr.Core.Infra.Autostart;

// Reading an entry means parsing XML a vendor tool wrote, so the two rules that matter are shared
// here: names are matched without their namespace (an exported task carries the schema one, a
// schtasks answer usually does not), and a DOCTYPE is skipped rather than resolved — a plist names a
// public DTD and a diagnostic must never reach for the network to read a file off the disk.
internal static class UnitXml
{
    internal static XDocument? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    internal static IEnumerable<XElement> LocalDescendants(XContainer root, string localName) =>
        root.Descendants().Where(element => element.Name.LocalName == localName);

    internal static string? Value(XContainer? parent, string localName) =>
        parent?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == localName)
            ?.Value;
}
