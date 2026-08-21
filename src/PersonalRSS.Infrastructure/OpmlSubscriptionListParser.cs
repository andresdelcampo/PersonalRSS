using System.Xml;
using System.Xml.Linq;
using PersonalRSS.Application;

namespace PersonalRSS.Infrastructure;

public sealed class OpmlSubscriptionListParser : ISubscriptionListParser
{
    public async Task<IReadOnlyList<SubscriptionCandidate>> ParseAsync(Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 10_000_000
            };
            using var reader = XmlReader.Create(content, settings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The uploaded file is not an OPML document.");

            var body = root.Elements().FirstOrDefault(element => element.Name.LocalName == "body")
                ?? throw new InvalidDataException("The OPML document does not contain a body.");
            var results = new List<SubscriptionCandidate>();
            Walk(body.Elements().Where(IsOutline), null, results);
            return results;
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The OPML document contains invalid XML.", exception);
        }
    }

    private static void Walk(IEnumerable<XElement> outlines, string? folder, ICollection<SubscriptionCandidate> results)
    {
        foreach (var outline in outlines)
        {
            var xmlUrl = Attribute(outline, "xmlUrl");
            var name = Attribute(outline, "title") ?? Attribute(outline, "text") ?? xmlUrl ?? "Untitled feed";
            if (!string.IsNullOrWhiteSpace(xmlUrl))
            {
                results.Add(new SubscriptionCandidate(name, xmlUrl, folder));
                continue;
            }

            var nested = outline.Elements().Where(IsOutline).ToList();
            if (nested.Count > 0) Walk(nested, JoinFolder(folder, name), results);
        }
    }

    private static bool IsOutline(XElement element) => element.Name.LocalName == "outline";
    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
    private static string JoinFolder(string? parent, string name) => string.IsNullOrWhiteSpace(parent) ? name : $"{parent}/{name}";
}
