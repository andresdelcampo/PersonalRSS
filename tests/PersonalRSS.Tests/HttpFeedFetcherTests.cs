using System.Net;
using System.Text;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class HttpFeedFetcherTests
{
    [Fact]
    public async Task Captures_standard_media_image_in_summary()
    {
        const string rss = """
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/"><channel><title>Example</title><item>
              <title>Article</title><link>https://example.test/article</link><guid>one</guid>
              <description>A useful summary.</description>
              <media:content medium="image" type="image/jpeg" url="https://cdn.example.test/article.jpg" />
            </item></channel></rss>
            """;
        using var client = new HttpClient(new StaticResponseHandler(rss));
        var source = new FeedSource { Name = "Example", Slug = "example", Url = "https://example.test/rss" };

        var articles = await new HttpFeedFetcher(client).FetchAsync(source);

        Assert.Contains("https://cdn.example.test/article.jpg", articles.Single().Summary);
        Assert.Contains("A useful summary.", articles.Single().Summary);
    }

    [Fact]
    public async Task Prefers_full_rss_content_over_description()
    {
        const string rss = """
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel><title>Example</title><item>
              <title>Article</title><link>https://example.test/article</link><guid>one</guid>
              <description>Short description.</description>
              <content:encoded><![CDATA[<p>Full article body.</p><img src="https://cdn.example.test/full.jpg">]]></content:encoded>
            </item></channel></rss>
            """;
        using var client = new HttpClient(new StaticResponseHandler(rss));
        var source = new FeedSource { Name = "Example", Slug = "example", Url = "https://example.test/rss" };

        var article = Assert.Single(await new HttpFeedFetcher(client).FetchAsync(source));

        Assert.Contains("Full article body.", article.Summary);
        Assert.Contains("https://cdn.example.test/full.jpg", article.Summary);
        Assert.DoesNotContain("Short description.", article.Summary);
    }

    [Fact]
    public async Task Prefers_full_atom_content_over_summary()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Example</title><entry>
              <title>Article</title><id>one</id><link href="https://example.test/article" />
              <summary type="html">Short summary.</summary>
              <content type="html">&lt;p&gt;Full Atom body.&lt;/p&gt;</content>
            </entry></feed>
            """;
        using var client = new HttpClient(new StaticResponseHandler(atom));
        var source = new FeedSource { Name = "Example", Slug = "example", Url = "https://example.test/atom" };

        var article = Assert.Single(await new HttpFeedFetcher(client).FetchAsync(source));

        Assert.Contains("Full Atom body.", article.Summary);
        Assert.DoesNotContain("Short summary.", article.Summary);
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/rss+xml")
            });
    }
}
