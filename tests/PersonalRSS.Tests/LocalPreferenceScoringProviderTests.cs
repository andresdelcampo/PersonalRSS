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
            Assert.Equal(1, relatedLike.MatchingFeedbackCount);
            Assert.InRange(relatedLike.Confidence, 0.01, 0.49);
            Assert.Contains("Low confidence", relatedLike.ConfidenceReason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void Relevance_bands_require_confidence_unless_feedback_is_explicit()
    {
        Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(0.9, 0.49));
        Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(0.1, 0.49));
        Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(0.65, 0.5));
        Assert.Equal(RelevanceBand.Filtered, RelevanceBands.Classify(0.35, 0.5));
        Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(0.2, 0, FeedbackKind.Interested));
        Assert.Equal(RelevanceBand.Filtered, RelevanceBands.Classify(0.8, 0, FeedbackKind.NotInterested));
    }

    [Fact]
    public async Task Gta_6_variants_produce_confident_positive_evidence_without_generic_word_matches()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-gta-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Games", Slug = "games", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var interested = Article(feed.Id, "GTA6 beach details impress Rockstar fans");
            var veryInterested = Article(feed.Id, "Grand Theft Auto VI release analysis");
            interested.Author = "Edwin Evans-Thirlwell";
            veryInterested.Author = "Edwin Evans-Thirlwell";
            var unrelated = Enumerable.Range(1, 30).Select(index =>
            {
                var article = Article(feed.Id, $"The weekly report form for unrelated item {index}");
                article.Author = "Edwin Evans-Thirlwell";
                return article;
            }).ToArray();
            await repository.UpsertArticlesAsync([interested, veryInterested, .. unrelated]);
            await repository.SetFeedbackAsync(interested.Id, FeedbackKind.Interested);
            await repository.SetFeedbackAsync(veryInterested.Id, FeedbackKind.VeryInterested);
            foreach (var article in unrelated)
                await repository.SetFeedbackAsync(article.Id, article.Title.GetHashCode() % 2 == 0 ? FeedbackKind.Interested : FeedbackKind.NotInterested);

            var options = Options.Create(new ScoringOptions
            {
                BaseScore = 0.5,
                PreferenceLearning = new PreferenceLearningOptions { EvidenceForFullConfidence = 6 }
            });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var result = await provider.ScoreAsync(new ArticleCandidate(
                "new-gta", "This week in PC games: Gamescom and GTA 6 fight for our attention", "https://example.test/gta", null, "Edwin Evans-Thirlwell",
                DateTimeOffset.UtcNow, feed.Id, feed.Name));

            Assert.Equal(2, result.MatchingFeedbackCount);
            Assert.Equal(0.5, result.Confidence, 3);
            Assert.True(result.Value >= RelevanceBands.HighScore);
            Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(result.Value, result.Confidence));
            Assert.Contains("GTA 6 +", result.Reason);
            Assert.DoesNotContain(" from for", result.Reason);
            Assert.DoesNotContain(", the", result.Reason);
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
