using System.Text;
using System.Xml;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class RssFeedRenderer : IFilteredFeedRenderer
{
    public string Render(FeedSource source, IReadOnlyList<Article> articles, Uri publicFeedUri)
    {
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true }))
        {
            writer.WriteStartDocument(); writer.WriteStartElement("rss"); writer.WriteAttributeString("version", "2.0"); writer.WriteStartElement("channel");
            writer.WriteElementString("title", $"{source.Name} — PersonalRSS"); writer.WriteElementString("link", publicFeedUri.ToString());
            writer.WriteElementString("description", $"Filtered articles from {source.Name}");
            foreach (var article in articles)
            {
                var ratingUri = new Uri(publicFeedUri, $"/preview/{Uri.EscapeDataString(source.Slug)}#article-{article.Id:N}");
                var description = $"{article.Summary}<p><a href=\"{ratingUri}\">Rate this article in PersonalRSS</a></p>";
                writer.WriteStartElement("item"); writer.WriteElementString("guid", article.ExternalId); writer.WriteElementString("title", article.Title);
                writer.WriteElementString("link", article.Link);
                writer.WriteElementString("description", description);
                if (!string.IsNullOrWhiteSpace(article.Author)) writer.WriteElementString("author", article.Author);
                writer.WriteElementString("pubDate", article.PublishedAt.ToUniversalTime().ToString("R")); writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
        }
        return output.ToString();
    }
}
