using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class SqliteFeedRepositoryTests
{
    [Fact]
    public async Task Initialize_adds_last_viewed_column_to_an_existing_database()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-legacy-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE Feeds (Id TEXT NOT NULL PRIMARY KEY); CREATE TABLE Articles (Id TEXT NOT NULL PRIMARY KEY, Score REAL NOT NULL, ScoreReason TEXT NULL); INSERT INTO Articles (Id, Score, ScoreReason) VALUES ('article-1', 0.73, 'Legacy score.');";
                await create.ExecuteNonQueryAsync();
            }
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;

            await new SqliteFeedRepository(new TestContextFactory(options)).InitializeAsync();

            await using var verify = new SqliteConnection($"Data Source={databasePath}");
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Feeds') WHERE name = 'LastViewedAt';";
            Assert.Equal(1L, await check.ExecuteScalarAsync());
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Articles') WHERE name IN ('ReadAt', 'IsUnreadPinned');";
            Assert.Equal(2L, await check.ExecuteScalarAsync());
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Articles') WHERE name IN ('AutomaticConfidence', 'MatchingFeedbackCount', 'ConfidenceReason');";
            Assert.Equal(3L, await check.ExecuteScalarAsync());
            check.CommandText = "SELECT BaselineScore || '|' || AutomaticScore || '|' || BaselineScoreReason || '|' || AutomaticScoreReason FROM Articles WHERE Id = 'article-1';";
            Assert.Equal("0.73|0.73|Legacy score.|Legacy score.", await check.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Article_read_state_supports_manual_unread_protection_and_bulk_read_actions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-read-state-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Test", Slug = "test", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            await repository.UpsertArticlesAsync([
                Article(feed.Id, "first", DateTimeOffset.UtcNow.AddHours(-2)),
                Article(feed.Id, "second", DateTimeOffset.UtcNow.AddHours(-1))
            ]);
            var articles = await repository.GetArticlesAsync(feed.Id, 0, 100);
            await repository.MarkFeedViewedAsync(feed.Id, DateTimeOffset.UtcNow);

            Assert.True(await repository.SetArticleReadStateAsync(articles[0].Id, true, false, DateTimeOffset.UtcNow));
            var unread = await repository.GetUnreadCountsAsync();
            Assert.Equal(1, unread[feed.Id]);
            var rated = await repository.GetArticlesAsync(feed.Id, 0, 100);
            Assert.True(rated.Single(article => article.Id == articles[0].Id).IsUnread);
            Assert.True(rated.Single(article => article.Id == articles[0].Id).IsUnreadPinned);

            Assert.Equal(0, await repository.MarkArticlesReadAsync([articles[0].Id], true, DateTimeOffset.UtcNow));
            unread = await repository.GetUnreadCountsAsync();
            Assert.Equal(1, unread[feed.Id]);

            Assert.Equal(1, await repository.MarkArticlesReadAsync([articles[0].Id], false, DateTimeOffset.UtcNow));
            Assert.False((await repository.GetUnreadCountsAsync()).ContainsKey(feed.Id));

            Assert.Equal(1, await repository.SetArticlesReadStateAsync([articles[0].Id], true, false, DateTimeOffset.UtcNow));
            var batchUnread = await repository.GetArticlesAsync(feed.Id, 0, 100);
            Assert.True(batchUnread.Single(article => article.Id == articles[0].Id).IsUnread);
            Assert.True(batchUnread.Single(article => article.Id == articles[0].Id).IsUnreadPinned);
            Assert.Equal(1, await repository.SetArticlesReadStateAsync([articles[0].Id], false, false, DateTimeOffset.UtcNow));

            await repository.SetArticleReadStateAsync(articles[1].Id, true, false, DateTimeOffset.UtcNow);
            Assert.True(await repository.MarkFeedViewedAsync(feed.Id, DateTimeOffset.UtcNow));
            Assert.False((await repository.GetUnreadCountsAsync()).ContainsKey(feed.Id));
            Assert.False((await repository.GetArticlesAsync(feed.Id, 0, 100)).Single(article => article.Id == articles[1].Id).IsUnreadPinned);

            Assert.True(await repository.MarkFeedUnreadAsync(feed.Id));
            Assert.Equal(2, (await repository.GetUnreadCountsAsync())[feed.Id]);
            var reset = await repository.GetArticlesAsync(feed.Id, 0, 100);
            Assert.All(reset, article => Assert.True(article.IsUnread));
            Assert.All(reset, article => Assert.False(article.IsUnreadPinned));
            Assert.False(await repository.MarkFeedUnreadAsync(Guid.NewGuid()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Unread_counts_can_match_the_default_preview_score()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-filtered-unread-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Test", Slug = "test", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            await repository.UpsertArticlesAsync([
                Article(feed.Id, "below-filter", DateTimeOffset.UtcNow.AddMinutes(-2), 0.4),
                Article(feed.Id, "visible", DateTimeOffset.UtcNow.AddMinutes(-1), 0.8)
            ]);

            var allUnread = await repository.GetUnreadCountsAsync();
            var visibleUnread = await repository.GetUnreadCountsAsync(0.5);

            Assert.Equal(2, allUnread[feed.Id]);
            Assert.Equal(1, visibleUnread[feed.Id]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Unread_counts_can_follow_confidence_aware_relevance_bands()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-band-unread-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Test", Slug = "test", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var high = Article(feed.Id, "high", DateTimeOffset.UtcNow, 0.8);
            high.AutomaticConfidence = 0.5;
            var uncertain = Article(feed.Id, "uncertain", DateTimeOffset.UtcNow, 0.8);
            uncertain.AutomaticConfidence = 0.49;
            var filtered = Article(feed.Id, "filtered", DateTimeOffset.UtcNow, 0.2);
            filtered.AutomaticConfidence = 0.5;
            await repository.UpsertArticlesAsync([high, uncertain, filtered]);

            Assert.Equal(1, (await repository.GetUnreadCountsByBandAsync(RelevanceBand.High))[feed.Id]);
            Assert.Equal(1, (await repository.GetUnreadCountsByBandAsync(RelevanceBand.Maybe))[feed.Id]);
            Assert.Equal(1, (await repository.GetUnreadCountsByBandAsync(RelevanceBand.Filtered))[feed.Id]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Delete_feed_removes_its_articles_and_feedback()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-delete-feed-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var removedFeed = new FeedSource { Name = "Remove me", Slug = "remove-me", Url = "https://example.test/remove.xml" };
            var retainedFeed = new FeedSource { Name = "Keep me", Slug = "keep-me", Url = "https://example.test/keep.xml" };
            await repository.AddFeedsAsync([removedFeed, retainedFeed]);
            await repository.UpsertArticlesAsync([
                Article(removedFeed.Id, "removed article", DateTimeOffset.UtcNow),
                Article(retainedFeed.Id, "retained article", DateTimeOffset.UtcNow)
            ]);
            var removedArticle = (await repository.GetArticlesAsync(removedFeed.Id, 0, 100)).Single();
            await repository.SetFeedbackAsync(removedArticle.Id, FeedbackKind.Interested);

            Assert.True(await repository.DeleteFeedAsync(removedFeed.Id));
            Assert.Null(await repository.GetFeedAsync(removedFeed.Id));
            Assert.NotNull(await repository.GetFeedAsync(retainedFeed.Id));
            Assert.Empty(await repository.GetArticlesAsync(removedFeed.Id, 0, 100));
            Assert.Single(await repository.GetArticlesAsync(retainedFeed.Id, 0, 100));
            await using (var verify = new PersonalRssDbContext(options))
                Assert.Empty(await verify.Feedback.Where(item => item.ArticleId == removedArticle.Id).ToListAsync());
            Assert.False(await repository.DeleteFeedAsync(removedFeed.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

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

            var unreadBeforeViewing = await repository.GetUnreadCountsAsync();
            Assert.Equal(2, unreadBeforeViewing[feed.Id]);
            Assert.True(await repository.MarkFeedViewedAsync(feed.Id, DateTimeOffset.UtcNow.AddMinutes(1)));
            var unreadAfterViewing = await repository.GetUnreadCountsAsync();
            Assert.False(unreadAfterViewing.ContainsKey(feed.Id));

            await repository.SetFeedbackAsync(articles[1].Id, FeedbackKind.NotInterested);
            var filtered = await repository.GetArticlesAsync(feed.Id, 0.5, 100);
            var overridden = await repository.GetArticleAsync(articles[1].Id);
            var rated = await repository.GetArticlesAsync(feed.Id, 0, 100);

            Assert.DoesNotContain(filtered, article => article.Id == articles[1].Id);
            Assert.Equal(0.1, overridden?.Score);
            Assert.Equal(0.5, overridden?.BaselineScore);
            Assert.Equal(0.5, overridden?.AutomaticScore);
            Assert.Contains("not interesting", overridden?.ScoreReason);
            Assert.Equal(FeedbackKind.NotInterested, rated.Single(article => article.Id == articles[1].Id).ActiveFeedback);

            await repository.SetFeedbackAsync(articles[1].Id, FeedbackKind.Interested);
            rated = await repository.GetArticlesAsync(feed.Id, 0, 100);
            Assert.Equal(FeedbackKind.Interested, rated.Single(article => article.Id == articles[1].Id).ActiveFeedback);
            Assert.Equal(0.9, rated.Single(article => article.Id == articles[1].Id).Score);
            await using (var verifyFeedback = new PersonalRssDbContext(options))
                Assert.Equal(1, await verifyFeedback.Feedback.CountAsync(item => item.ArticleId == articles[1].Id));

            await repository.ClearFeedbackAsync(articles[1].Id);
            rated = await repository.GetArticlesAsync(feed.Id, 0, 100);
            Assert.Null(rated.Single(article => article.Id == articles[1].Id).ActiveFeedback);
            Assert.Equal(0.5, rated.Single(article => article.Id == articles[1].Id).Score);
            Assert.Equal("Automatic baseline.", rated.Single(article => article.Id == articles[1].Id).ScoreReason);
            await using (var verifyFeedback = new PersonalRssDbContext(options))
                Assert.Empty(await verifyFeedback.Feedback.Where(item => item.ArticleId == articles[1].Id).ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static Article Article(Guid feedId, string title, DateTimeOffset publishedAt, double score = 0.5) => new()
    {
        Id = Guid.NewGuid(),
        FeedSourceId = feedId,
        ExternalId = title,
        Title = title,
        Link = $"https://example.test/{title}",
        PublishedAt = publishedAt,
        BaselineScore = score,
        BaselineScoreReason = "Configured baseline.",
        AutomaticScore = score,
        AutomaticScoreReason = "Automatic baseline.",
        Score = score,
        ScoreReason = "Automatic baseline."
    };

    private sealed class TestContextFactory(DbContextOptions<PersonalRssDbContext> options) : IDbContextFactory<PersonalRssDbContext>
    {
        public PersonalRssDbContext CreateDbContext() => new(options);
    }
}
