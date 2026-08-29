using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
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
            for (var index = 0; index < unrelated.Length; index++)
                await repository.SetFeedbackAsync(unrelated[index].Id, index % 2 == 0 ? FeedbackKind.Interested : FeedbackKind.NotInterested);

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

    [Fact]
    public async Task Generic_single_words_and_shared_feed_do_not_create_confident_recommendations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-generic-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Hacker News", Slug = "hacker-news", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var genericLikes = Enumerable.Range(1, 15).Select(index => Article(feed.Id, $"Useful app news item {index}")).ToArray();
            await repository.UpsertArticlesAsync(genericLikes);
            foreach (var article in genericLikes) await repository.SetFeedbackAsync(article.Id, FeedbackKind.Interested);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var result = await provider.ScoreAsync(new ArticleCandidate(
                "generic", "It works better in the app", "https://example.test/generic", null, null,
                DateTimeOffset.UtcNow, feed.Id, feed.Name));

            Assert.Equal(0.5, result.Value);
            Assert.Equal(0, result.Confidence);
            Assert.Equal(0, result.MatchingFeedbackCount);
            Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(result.Value, result.Confidence));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Feed_metadata_urls_and_counters_are_not_learning_evidence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-metadata-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Hacker News", Slug = "hacker-news", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var likes = Enumerable.Range(1, 12).Select(index =>
            {
                var article = Article(feed.Id, $"Unrelated liked topic {index}");
                article.Summary = $"Article URL: https://example.test/{index} Comments URL: https://news.ycombinator.com/item?id={index} Points: 104 # Comments: 37";
                return article;
            }).ToArray();
            await repository.UpsertArticlesAsync(likes);
            foreach (var article in likes) await repository.SetFeedbackAsync(article.Id, FeedbackKind.Interested);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var result = await provider.ScoreAsync(new ArticleCandidate(
                "candidate", "Completely different subject", "https://example.test/candidate",
                "Article URL: https://other.test/a Comments URL: https://news.ycombinator.com/item?id=999 Points: 104 # Comments: 37",
                null, DateTimeOffset.UtcNow, feed.Id, feed.Name));

            Assert.Equal(0.5, result.Value);
            Assert.Equal(0, result.Confidence);
            Assert.Equal(0, result.MatchingFeedbackCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Repeated_distinctive_topic_can_be_learned_without_an_external_model()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-distinctive-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "AI", Slug = "ai", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var likes = Enumerable.Range(1, 6).Select(index => Article(feed.Id, $"Claude workflow example {index}")).ToArray();
            var unrelated = Enumerable.Range(1, 24).Select(index => Article(feed.Id, $"Unrelated cooking note {index}")).ToArray();
            await repository.UpsertArticlesAsync([.. likes, .. unrelated]);
            foreach (var article in likes) await repository.SetFeedbackAsync(article.Id, FeedbackKind.Interested);
            foreach (var article in unrelated)
                await repository.SetFeedbackAsync(article.Id, article.Title.GetHashCode() % 2 == 0 ? FeedbackKind.Interested : FeedbackKind.NotInterested);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var result = await provider.ScoreAsync(new ArticleCandidate(
                "claude", "Claude desktop tools", "https://example.test/claude", null, null,
                DateTimeOffset.UtcNow, feed.Id, feed.Name));

            Assert.True(result.Value >= RelevanceBands.HighScore);
            Assert.True(result.Confidence >= RelevanceBands.RequiredConfidence);
            Assert.Equal(6, result.MatchingFeedbackCount);
            Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(result.Value, result.Confidence));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Rescoring_updates_stored_predictions_but_preserves_manual_scores()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-rescoring-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Retro", Slug = "retro", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var examples = Enumerable.Range(1, 3).Select(index => Article(feed.Id, $"Amiga hardware preservation project {index}")).ToArray();
            var predicted = Article(feed.Id, "New Amiga hardware preservation work");
            var manual = Article(feed.Id, "Celebrity fashion awards");
            await repository.UpsertArticlesAsync([.. examples, predicted, manual]);
            foreach (var article in examples) await repository.SetFeedbackAsync(article.Id, FeedbackKind.VeryInterested);
            await repository.SetFeedbackAsync(manual.Id, FeedbackKind.NotInterested);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var service = new PreferenceRescoringService(repository, provider);
            var updated = await service.RescoreAsync();

            var rescoredPrediction = await repository.GetArticleAsync(predicted.Id);
            var preservedManual = await repository.GetArticleAsync(manual.Id);
            Assert.Equal(5, updated);
            Assert.NotNull(rescoredPrediction);
            Assert.True(rescoredPrediction.AutomaticScore >= RelevanceBands.HighScore);
            Assert.True(rescoredPrediction.AutomaticConfidence >= RelevanceBands.RequiredConfidence);
            Assert.Equal(rescoredPrediction.AutomaticScore, rescoredPrediction.Score);
            Assert.NotNull(preservedManual);
            Assert.Equal(0.1, preservedManual.Score);
            Assert.Contains("not interesting", preservedManual.ScoreReason, StringComparison.OrdinalIgnoreCase);
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
