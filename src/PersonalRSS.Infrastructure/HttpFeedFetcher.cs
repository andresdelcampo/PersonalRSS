using System.Xml.Linq;
using System.Net;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class HttpFeedFetcher(HttpClient httpClient) : IFeedFetcher
{
    public async Task<IReadOnlyList<ArticleCandidate>> FetchAsync(FeedSource source, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        return document.Root?.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase) == true ? ParseAtom(document) : ParseRss(document);
    }

    private static IReadOnlyList<ArticleCandidate> ParseRss(XDocument document) =>
        document.Descendants().Where(x => x.Name.LocalName == "item").Select(item =>
        {
            var link = Value(item, "link") ?? string.Empty;
            return new ArticleCandidate(Value(item, "guid") ?? link, Value(item, "title") ?? "Untitled", link,
                SummaryWithImage(item, Value(item, "encoded") ?? Value(item, "description")), Value(item, "creator") ?? Value(item, "author"), ParseDate(Value(item, "pubDate")));
        }).Where(x => !string.IsNullOrWhiteSpace(x.Link)).ToList();

    private static IReadOnlyList<ArticleCandidate> ParseAtom(XDocument document) =>
        document.Descendants().Where(x => x.Name.LocalName == "entry").Select(entry =>
        {
            var link = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "link" && ((string?)x.Attribute("rel") is null or "alternate"))?.Attribute("href")?.Value ?? string.Empty;
            return new ArticleCandidate(Value(entry, "id") ?? link, Value(entry, "title") ?? "Untitled", link,
                SummaryWithImage(entry, Value(entry, "content") ?? Value(entry, "summary")),
                entry.Elements().FirstOrDefault(x => x.Name.LocalName == "author")?.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value,
                ParseDate(Value(entry, "published") ?? Value(entry, "updated")));
        }).Where(x => !string.IsNullOrWhiteSpace(x.Link)).ToList();

    private static string? Value(XElement parent, string localName) => parent.Elements().FirstOrDefault(x => x.Name.LocalName == localName)?.Value?.Trim();
    private static string? SummaryWithImage(XElement parent, string? summary)
    {
        if (summary?.Contains("<img", StringComparison.OrdinalIgnoreCase) == true) return summary;
        var media = parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "thumbnail" ||
            ((element.Name.LocalName is "content" or "enclosure") &&
            ((string?)element.Attribute("medium") == "image" ||
             ((string?)element.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)));
        var imageUrl = (string?)media?.Attribute("url");
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            var imageLink = parent.Elements().FirstOrDefault(element => element.Name.LocalName == "link" &&
                (string?)element.Attribute("rel") == "enclosure" &&
                ((string?)element.Attribute("type"))?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
            imageUrl = (string?)imageLink?.Attribute("href");
        }
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme is not ("http" or "https")) return summary;
        return $"<img src=\"{WebUtility.HtmlEncode(imageUri.ToString())}\" alt=\"\">{summary}";
    }
    private static DateTimeOffset ParseDate(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
