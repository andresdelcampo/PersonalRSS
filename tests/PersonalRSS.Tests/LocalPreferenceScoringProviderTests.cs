using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class LocalPreferenceScoringProviderTests
{
    [Fact]
    public async Task Related_positive_and_negative_feedback_changes_future_scores_with_explanations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Technology", Slug = "technology", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var liked = Article(feed.Id, "Amiga FPGA hardware preservation project");
            var disliked = Article(feed.Id, "Celebrity fashion awards and red carpet outfits");
            await repository.UpsertArticlesAsync([liked, disliked]);
            await repository.SetFeedbackAsync(liked.Id, FeedbackKind.VeryInterested);
            await repository.SetFeedbackAsync(disliked.Id, FeedbackKind.NeverThisTopic);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var relatedLike = await provider.ScoreAsync(new ArticleCandidate("new-like", "New Amiga hardware preservation work", "https://example.test/like", null, null, DateTimeOffset.UtcNow, feed.Id, feed.Name));
            var relatedDislike = await provider.ScoreAsync(new ArticleCandidate("new-dislike", "Celebrity fashion returns to the red carpet", "https://example.test/dislike", null, null, DateTimeOffset.UtcNow, feed.Id, feed.Name));

            Assert.True(relatedLike.Value > 0.5);
            Assert.True(relatedDislike.Value < 0.5);
            Assert.Equal(0.5, relatedLike.BaselineValue);
            Assert.Contains("Personal model +", relatedLike.Reason);
            Assert.Contains("Personal model -", relatedDislike.Reason);
            Assert.Contains("matching feedback choice", relatedLike.Reason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static Article Article(Guid feedId, string title) => new()
    {
        Id = Guid.NewGuid(),
        FeedSourceId = feedId,
        ExternalId = title,
        Title = title,
        Link = $"https://example.test/{Guid.NewGuid():N}",
        PublishedAt = DateTimeOffset.UtcNow,
        BaselineScore = 0.5,
        BaselineScoreReason = "Baseline.",
        AutomaticScore = 0.5,
        AutomaticScoreReason = "Baseline.",
        Score = 0.5,
        ScoreReason = "Baseline."
    };

    private sealed class TestContextFactory(DbContextOptions<PersonalRssDbContext> options) : IDbContextFactory<PersonalRssDbContext>
    {
        public PersonalRssDbContext CreateDbContext() => new(options);
    }
}
