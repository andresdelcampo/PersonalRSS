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

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/rss+xml")
            });
    }
}
