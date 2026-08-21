using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Tests;

public sealed class FeedImportServiceTests
{
    [Fact]
    public async Task Reimport_skips_existing_and_duplicate_urls()
    {
        var repository = new FakeRepository([new FeedSource { Name = "Existing", Slug = "news", Url = "https://example.test/feed/" }]);
        var parser = new StubParser([
            new("Existing copy", "https://example.test/feed", null),
            new("News", "https://new.test/rss", "Technology"),
            new("News duplicate", "https://new.test/rss", null),
            new("Broken", "not-a-url", null)
        ]);

        var result = await new FeedImportService(repository, parser).ImportAsync(Stream.Null);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Invalid);
        Assert.Equal("news-2", repository.Feeds.Single(feed => feed.Url == "https://new.test/rss").Slug);
    }

    private sealed class StubParser(IReadOnlyList<SubscriptionCandidate> subscriptions) : ISubscriptionListParser
    {
        public Task<IReadOnlyList<SubscriptionCandidate>> ParseAsync(Stream content, CancellationToken cancellationToken = default) => Task.FromResult(subscriptions);
    }

    private sealed class FakeRepository(IEnumerable<FeedSource> feeds) : IFeedRepository
    {
        public List<FeedSource> Feeds { get; } = [.. feeds];
        public Task<IReadOnlyList<FeedSource>> GetFeedsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FeedSource>>(Feeds);
        public Task AddFeedsAsync(IEnumerable<FeedSource> additions, CancellationToken cancellationToken = default) { Feeds.AddRange(additions); return Task.CompletedTask; }
        public Task AddFeedAsync(FeedSource feed, CancellationToken cancellationToken = default) { Feeds.Add(feed); return Task.CompletedTask; }
        public Task<FeedSource?> GetFeedAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Feeds.SingleOrDefault(feed => feed.Id == id));
        public Task<FeedSource?> GetFeedBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(Feeds.SingleOrDefault(feed => feed.Slug == slug));
        public Task SaveFeedAsync(FeedSource feed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<Article?> GetArticleAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Article?>(null);
        public Task<IReadOnlyList<Article>> GetArticlesAsync(Guid? feedId, double minimumScore, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Article>>([]);
        public Task AddFeedbackAsync(ArticleFeedback feedback, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
