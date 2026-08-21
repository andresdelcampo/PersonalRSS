using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class SqliteFeedRepositoryTests
{
    [Fact]
    public async Task Articles_are_returned_newest_first()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Test", Slug = "test", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var firstInsertCount = await repository.UpsertArticlesAsync([
                Article(feed.Id, "older", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                Article(feed.Id, "newer", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            ]);
            var repeatInsertCount = await repository.UpsertArticlesAsync(await repository.GetArticlesAsync(feed.Id, 0, 100));

            var articles = await repository.GetArticlesAsync(feed.Id, 0, 100);

            Assert.Equal(["newer", "older"], articles.Select(article => article.Title));
            Assert.Equal(2, firstInsertCount);
            Assert.Equal(0, repeatInsertCount);

            await repository.AddFeedbackAsync(new ArticleFeedback { ArticleId = articles[1].Id, Kind = FeedbackKind.NotInterested });
            var filtered = await repository.GetArticlesAsync(feed.Id, 0.5, 100);
            var overridden = await repository.GetArticleAsync(articles[1].Id);

            Assert.DoesNotContain(filtered, article => article.Id == articles[1].Id);
            Assert.Equal(0, overridden?.Score);
            Assert.Contains("not interesting", overridden?.ScoreReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static Article Article(Guid feedId, string title, DateTimeOffset publishedAt) => new()
    {
        Id = Guid.NewGuid(), FeedSourceId = feedId, ExternalId = title, Title = title,
        Link = $"https://example.test/{title}", PublishedAt = publishedAt, Score = 0.5
    };

    private sealed class TestContextFactory(DbContextOptions<PersonalRssDbContext> options) : IDbContextFactory<PersonalRssDbContext>
    {
        public PersonalRssDbContext CreateDbContext() => new(options);
    }
}
