using System.Xml.Linq;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class RssFeedRendererTests
{
    [Fact]
    public void Render_returns_complete_rss_document()
    {
        var source = new FeedSource { Name = "Example", Slug = "example", Url = "https://example.test/rss" };
        var article = new Article
        {
            Id = Guid.NewGuid(), FeedSourceId = source.Id, ExternalId = "article-1", Title = "First article",
            Link = "https://example.test/article-1", PublishedAt = DateTimeOffset.UtcNow, Score = 0.8
        };

        var xml = new RssFeedRenderer().Render(source, [article], new Uri("http://localhost/feeds/example.xml"));
        var document = XDocument.Parse(xml);

        Assert.Equal("rss", document.Root?.Name.LocalName);
        Assert.Equal("2.0", document.Root?.Attribute("version")?.Value);
        Assert.Equal("First article", document.Descendants("item").Single().Element("title")?.Value);
    }
}
