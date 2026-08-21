using System.Text;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class OpmlSubscriptionListParserTests
{
    [Fact]
    public async Task Parses_nested_folders_and_feed_titles()
    {
        const string opml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0"><head><title>Subscriptions</title></head><body>
              <outline text="Technology">
                <outline text="Example Feed" title="Example RSS" type="rss" xmlUrl="https://example.test/feed.xml" htmlUrl="https://example.test/" />
              </outline>
              <outline text="Standalone" xmlUrl="https://standalone.test/rss" />
            </body></opml>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(opml));

        var subscriptions = await new OpmlSubscriptionListParser().ParseAsync(stream);

        Assert.Equal(2, subscriptions.Count);
        Assert.Equal("Example RSS", subscriptions[0].Name);
        Assert.Equal("Technology", subscriptions[0].Folder);
        Assert.Equal("https://standalone.test/rss", subscriptions[1].Url);
    }

    [Fact]
    public async Task Rejects_non_opml_xml()
    {
        await using var stream = new MemoryStream("<feeds />"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => new OpmlSubscriptionListParser().ParseAsync(stream));
    }
}
