using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class LocalAiIntegrationTests
{
    [Fact]
    public void Ling_response_parser_accepts_json_fences_and_rejects_partial_contracts()
    {
        const string valid = """
            {"output":[{"type":"reasoning","content":"hidden"},{"type":"message","content":"```json\n{\"direction\":\"positive\",\"score\":0.88,\"confidence\":0.91,\"matchedExample\":\"P1\",\"reason\":\"Same preservation topic.\"}\n```"}]}
            """;
        const string invalid = """{"output":[{"type":"message","content":"{\"direction\":\"positive\"}"}]}""";

        Assert.True(LocalAiReviewService.TryParseMessage(valid, out var parsed, out _));
        Assert.Equal("positive", parsed?.Direction);
        Assert.Equal(0.91, parsed?.Confidence);
        Assert.False(LocalAiReviewService.TryParseMessage(invalid, out _, out _));
    }

    [Fact]
    public async Task Unload_model_removes_only_instances_loaded_by_the_app()
    {
        var unloaded = new List<string>();
        var handler = new RecordingHandler(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/load")
                return await Task.FromResult(JsonResponse("{\"instance_id\":\"owned-1\"}"));

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/unload")
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var json = System.Text.Json.JsonDocument.Parse(body);
                unloaded.Add(json.RootElement.GetProperty("instance_id").GetString()!);
                return await Task.FromResult(JsonResponse("{}"));
            }

            return await Task.FromResult(JsonResponse("{}", HttpStatusCode.NotFound));
        });
        var service = new LmStudioControlService(new TestHttpClientFactory(handler));

        await service.LoadModelAsync("http://localhost:1234/api/v1/chat", "old-model");
        Assert.True(await service.UnloadOwnedModelAsync("http://localhost:1234/api/v1/chat", "old-model"));
        Assert.Equal(["owned-1"], unloaded);
    }

    [Fact]
    public async Task Unload_model_leaves_preexisting_instances_alone()
    {
        var unloadRequests = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/models/unload")
                unloadRequests++;
            return Task.FromResult(JsonResponse("{}"));
        });
        var service = new LmStudioControlService(new TestHttpClientFactory(handler));

        Assert.True(await service.UnloadOwnedModelAsync("http://localhost:1234/api/v1/chat", "already-loaded-model"));
        Assert.Equal(0, unloadRequests);
    }

    [Theory]
    [InlineData("http://localhost:1234/api/v1/chat", true)]
    [InlineData("http://127.0.0.1:1234/api/v1/chat", true)]
    [InlineData("https://localhost:1234/api/v1/chat", false)]
    [InlineData("http://example.com/api/v1/chat", false)]
    public void Lm_studio_endpoint_is_restricted_to_local_http(string endpoint, bool expected) =>
        Assert.Equal(expected, LmStudioControlService.TryLocalEndpoint(endpoint, out _, out _));

    [Fact]
    public async Task Local_ai_setting_reversibly_applies_and_restores_the_local_score()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-ai-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Test", Slug = "test", Url = "https://example.test/feed" };
            await repository.AddFeedAsync(feed);
            var article = new Article
            {
                Id = Guid.NewGuid(), FeedSourceId = feed.Id, ExternalId = "one", Title = "One", Link = "https://example.test/one",
                PublishedAt = DateTimeOffset.UtcNow, BaselineScore = 0.5, AutomaticScore = 0.5, AutomaticScoreReason = "Local result.",
                AutomaticConfidence = 0.2, LocalAutomaticScore = 0.5, LocalAutomaticScoreReason = "Local result.", LocalAutomaticConfidence = 0.2,
                Score = 0.5, ScoreReason = "Local result."
            };
            await repository.UpsertArticlesAsync([article]);
            await repository.SaveLocalAiSettingsAsync(true, "http://localhost:1234/api/v1/chat", "ling-3.0-tiny");
            await repository.ApplyLocalAiAssessmentAsync(new LocalAiAssessment(article.Id, "positive", 0.82, 0.9,
                "Local AI positive match to P1.", "P1", "ling-3.0-tiny", DateTimeOffset.UtcNow));

            Assert.Equal(0.82, (await repository.GetArticleAsync(article.Id))?.AutomaticScore);
            await repository.SaveLocalAiSettingsAsync(false, "http://localhost:1234/api/v1/chat", "ling-3.0-tiny");
            Assert.Equal(0.5, (await repository.GetArticleAsync(article.Id))?.AutomaticScore);
            await repository.SaveLocalAiSettingsAsync(true, "http://localhost:1234/api/v1/chat", "ling-3.0-tiny");
            Assert.Equal(0.82, (await repository.GetArticleAsync(article.Id))?.AutomaticScore);
            await repository.SaveLocalAiSettingsAsync(true, "http://localhost:1234/api/v1/chat", "another-local-model");
            var afterModelChange = await repository.GetArticleAsync(article.Id);
            Assert.Equal(0.5, afterModelChange?.AutomaticScore);
            Assert.Null(afterModelChange?.LocalAiAssessedAt);
            var candidates = await repository.GetLocalAiReviewCandidatesAsync(12);
            Assert.Contains(candidates, candidate => candidate.ArticleId == article.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Review_preserves_vote_strength_and_accepts_only_a_calibrated_close_citation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"personalrss-ai-calibration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PersonalRssDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var repository = new SqliteFeedRepository(new TestContextFactory(options));
            await repository.InitializeAsync();
            var feed = new FeedSource { Name = "Example source", Slug = "example", Url = "https://example.test/feed" };
            await repository.AddFeedAsync(feed);
            var kinds = new[]
            {
                (FeedbackKind.VeryInterested, "vintage computer restoration", "Archivist"),
                (FeedbackKind.Interested, "retro software preservation", "Curator"),
                (FeedbackKind.NotInterested, "celebrity sports update", "Wire desk"),
                (FeedbackKind.NeverThisTopic, "cryptocurrency promotion scheme", "Token desk")
            };
            var published = DateTimeOffset.UtcNow.AddDays(-20);
            foreach (var (kind, topic, author) in kinds)
            {
                for (var index = 0; index < 12; index++)
                {
                    var article = new Article
                    {
                        Id = Guid.NewGuid(), FeedSourceId = feed.Id, ExternalId = $"{(int)kind}-{index}",
                        Title = $"{topic} report {index}", Link = $"https://example.test/{(int)kind}/{index}",
                        Summary = $"A detailed {topic} article for readers.", Author = author,
                        PublishedAt = published.AddMinutes(index), Score = 0.5
                    };
                    await repository.UpsertArticlesAsync([article]);
                    await repository.SetFeedbackAsync(article.Id, kind);
                }
            }

            var candidate = new Article
            {
                Id = Guid.NewGuid(), FeedSourceId = feed.Id, ExternalId = "candidate",
                Title = "cryptocurrency promotion scheme report 50", Link = "https://example.test/candidate",
                Summary = "A detailed cryptocurrency promotion scheme article for readers.", Author = "Token desk",
                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(1), Score = 0.5,
                AutomaticScore = 0.5, LocalAutomaticScore = 0.5
            };
            await repository.UpsertArticlesAsync([candidate]);
            await repository.SaveLocalAiSettingsAsync(true, "http://localhost:1234/api/v1/chat", "test-model");

            string? sentInput = null;
            var handler = new RecordingHandler(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var json = System.Text.Json.JsonDocument.Parse(body);
                sentInput = json.RootElement.GetProperty("input").GetString();
                return JsonResponse("""{"output":[{"type":"message","content":"{\"direction\":\"negative\",\"score\":0.1,\"confidence\":0.95,\"matchedExample\":\"N1\",\"reason\":\"Close match to an explicitly unwanted topic.\"}"}]}""");
            });
            var clients = new TestHttpClientFactory(handler);
            var scoring = Options.Create(new ScoringOptions
            {
                PreferenceLearning = new PreferenceLearningOptions
                {
                    MinimumCalibrationExamples = 20,
                    CalibrationFolds = 5,
                    MinimumCalibratedPredictions = 2,
                    TargetPrecision = 0.90,
                    MinimumExampleSimilarity = 0.20
                }
            });
            var review = new LocalAiReviewService(repository, clients, new LmStudioControlService(clients), scoring);

            var result = await review.ReviewUnreadAsync(1);

            var saved = await repository.GetArticleAsync(candidate.Id);
            Assert.True(result.Accepted == 1, $"Expected an accepted assessment, but got: {saved?.LocalAiReason}");
            Assert.Contains("[VERY INTERESTED]", sentInput);
            Assert.Contains("[INTERESTED]", sentInput);
            Assert.Contains("[NOT INTERESTED]", sentInput);
            Assert.Contains("[NEVER THIS TOPIC]", sentInput);
            Assert.Contains("Source: Example source", sentInput);
            Assert.Contains("Author: Token desk", sentInput);
            Assert.Equal("negative", saved?.LocalAiDirection);
            Assert.True(saved?.LocalAiScore <= RelevanceBands.FilteredScore);
            Assert.Contains("citation gate achieved", saved?.LocalAiReason);

            var unrelated = new Article
            {
                Id = Guid.NewGuid(), FeedSourceId = feed.Id, ExternalId = "unrelated",
                Title = "Municipal garden opening hours", Link = "https://example.test/unrelated",
                Summary = "The public garden published its autumn opening schedule.", Author = "City desk",
                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(2), Score = 0.5,
                AutomaticScore = 0.5, LocalAutomaticScore = 0.5
            };
            await repository.UpsertArticlesAsync([unrelated]);

            var rejected = await review.ReviewUnreadAsync(1);

            Assert.Equal(0, rejected.Accepted);
            Assert.Equal(1, rejected.Neutral);
            var rejectedArticle = await repository.GetArticleAsync(unrelated.Id);
            Assert.Equal("neutral", rejectedArticle?.LocalAiDirection);
            Assert.Null(rejectedArticle?.LocalAiScore);
            Assert.Contains("below the calibrated", rejectedArticle?.LocalAiReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class TestContextFactory(DbContextOptions<PersonalRssDbContext> options) : IDbContextFactory<PersonalRssDbContext>
    {
        public PersonalRssDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }

    private static HttpResponseMessage JsonResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };
}
