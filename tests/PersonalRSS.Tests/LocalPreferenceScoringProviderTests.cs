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
            for (var index = 0; index < unrelated.Length; index++)
                await repository.SetFeedbackAsync(unrelated[index].Id, index % 2 == 0 ? FeedbackKind.Interested : FeedbackKind.NotInterested);

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
    public async Task Duplicate_story_feedback_counts_once()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-duplicate-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var firstFeed = new FeedSource { Name = "First", Slug = "first", Url = "https://first.test/rss" };
            var secondFeed = new FeedSource { Name = "Second", Slug = "second", Url = "https://second.test/rss" };
            await repository.AddFeedAsync(firstFeed);
            await repository.AddFeedAsync(secondFeed);
            var first = Article(firstFeed.Id, "Amiga FPGA restoration project");
            var second = Article(secondFeed.Id, "Amiga FPGA restoration project");
            first.Link = "https://stories.test/amiga-fpga?utm_source=first";
            second.Link = "https://stories.test/amiga-fpga?utm_source=second";
            await repository.UpsertArticlesAsync([first, second]);
            await repository.SetFeedbackAsync(first.Id, FeedbackKind.VeryInterested);
            await repository.SetFeedbackAsync(second.Id, FeedbackKind.VeryInterested);

            var options = Options.Create(new ScoringOptions { BaseScore = 0.5 });
            var provider = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var result = await provider.ScoreAsync(new ArticleCandidate(
                "candidate", "New Amiga FPGA restoration project", "https://other.test/amiga", null, null,
                DateTimeOffset.UtcNow, firstFeed.Id, firstFeed.Name));

            Assert.Equal(1, result.MatchingFeedbackCount);
            Assert.True(result.Confidence < RelevanceBands.RequiredConfidence);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Precision_ensemble_requires_calibrated_classifier_agreement()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-ensemble-learning-test-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(dbOptions));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Mixed", Slug = "mixed", Url = "https://example.test/rss" };
            await repository.AddFeedAsync(feed);
            var positives = Enumerable.Range(1, 9).Select(index => Article(feed.Id, $"Amiga hardware restoration diary {index}")).ToArray();
            var negatives = Enumerable.Range(1, 9).Select(index => Article(feed.Id, $"Celebrity fashion awards roundup {index}")).ToArray();
            var unrelated = Enumerable.Range(1, 52).Select(index => Article(feed.Id, $"Distinct unrelated subject number {index} token{index}")).ToArray();
            await repository.UpsertArticlesAsync([.. positives, .. negatives, .. unrelated]);
            foreach (var article in positives) await repository.SetFeedbackAsync(article.Id, FeedbackKind.VeryInterested);
            foreach (var article in negatives) await repository.SetFeedbackAsync(article.Id, FeedbackKind.NeverThisTopic);
            for (var index = 0; index < unrelated.Length; index++)
                await repository.SetFeedbackAsync(unrelated[index].Id, index % 2 == 0 ? FeedbackKind.Interested : FeedbackKind.NotInterested);

            var options = Options.Create(new ScoringOptions
            {
                BaseScore = 0.5,
                PreferenceLearning = new PreferenceLearningOptions
                {
                    MinimumCalibrationExamples = 20,
                    MinimumCalibratedPredictions = 8,
                    TargetPrecision = 0.85
                }
            });
            var heuristic = new LocalPreferenceScoringProvider(new KeywordScoringProvider(options), repository, options);
            var provider = new PrecisionEnsembleScoringProvider(heuristic, repository, options);
            var positive = await provider.ScoreAsync(new ArticleCandidate(
                "positive", "Amiga hardware restoration continues", "https://example.test/amiga-new", null, null,
                DateTimeOffset.UtcNow, feed.Id, feed.Name));
            var negative = await provider.ScoreAsync(new ArticleCandidate(
                "negative", "Celebrity fashion awards return", "https://example.test/fashion-new", null, null,
                DateTimeOffset.UtcNow, feed.Id, feed.Name));
            var unrelatedCandidate = await provider.ScoreAsync(new ArticleCandidate(
                "unrelated", "An entirely unseen gardening subject", "https://example.test/garden", null, null,
                DateTimeOffset.UtcNow, feed.Id, feed.Name));
            var heldOut = (await provider.ScoreAsync([
                new ArticleCandidate(
                    positives[0].ExternalId, positives[0].Title, positives[0].Link, positives[0].Summary, positives[0].Author,
                    positives[0].PublishedAt, positives[0].FeedSourceId, feed.Name)
            ])).Single();

            Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(positive.Value, positive.Confidence));
            Assert.Contains("Precision ensemble agrees", positive.Reason);
            Assert.Equal(RelevanceBand.Filtered, RelevanceBands.Classify(negative.Value, negative.Confidence));
            Assert.Contains("Precision ensemble agrees", negative.Reason);
            Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(unrelatedCandidate.Value, unrelatedCandidate.Confidence));
            Assert.Contains("Both local models must agree", unrelatedCandidate.Reason);
            Assert.Contains("held-out feedback story", heldOut.Reason);
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
