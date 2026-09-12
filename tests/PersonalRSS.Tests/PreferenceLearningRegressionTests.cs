using System.Reflection;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class PreferenceLearningRegressionTests
{
    [Fact]
    public async Task Fresh_installations_start_neutral_and_independently_learn_opposite_preferences()
    {
        var (firstRepository, firstSnapshot) = Repository([]);
        var (secondRepository, secondSnapshot) = Repository([]);
        var first = Ensemble(firstRepository);
        var second = Ensemble(secondRepository);
        var candidate = Candidate("new-story", "Orchid cultivation");

        var fresh = await first.ScoreAsync(candidate);
        Assert.Equal(0.5, fresh.Value);
        Assert.Equal(0, fresh.Confidence);

        firstSnapshot.Examples = BalancedHistory("Orchid cultivation", "Bread baking");
        var firstLearned = await first.ScoreAsync(candidate);
        var secondStillFresh = await second.ScoreAsync(candidate);
        Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(firstLearned.Value, firstLearned.Confidence));
        Assert.Equal(0.5, secondStillFresh.Value);
        Assert.Equal(0, secondStillFresh.Confidence);

        secondSnapshot.Examples = firstSnapshot.Examples.Select(e => e with { Kind = (FeedbackKind)(-(int)e.Kind) }).ToArray();
        var secondLearned = await second.ScoreAsync(candidate);
        Assert.Equal(RelevanceBand.Filtered, RelevanceBands.Classify(secondLearned.Value, secondLearned.Confidence));
        Assert.Equal(firstLearned, await first.ScoreAsync(candidate));
    }

    [Theory]
    [InlineData("Amiga hardware", RelevanceBand.High)]
    [InlineData("Celebrity fashion", RelevanceBand.Filtered)]
    public async Task Frequent_distinctive_interests_survive_as_the_history_grows(string title, RelevanceBand expected)
    {
        var (repository, _) = Repository(BalancedHistory());
        var heuristic = Heuristic(repository);
        var candidate = Candidate("new-story", title);
        var similarity = await heuristic.ScoreAsync(candidate);
        var combined = await Ensemble(repository).ScoreAsync(candidate);

        Assert.Equal(expected, RelevanceBands.Classify(similarity.Value, similarity.Confidence));
        Assert.Equal(12, similarity.MatchingFeedbackCount);
        Assert.Equal(expected, RelevanceBands.Classify(combined.Value, combined.Confidence));
        Assert.Contains("Calibrated combined model", combined.Reason);
    }

    [Fact]
    public async Task Common_phrases_shared_by_both_classes_do_not_become_interests()
    {
        var (repository, _) = Repository(BalancedHistory());
        var result = await Ensemble(repository).ScoreAsync(Candidate("unrelated", "Weekly digest"));

        Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(result.Value, result.Confidence));
        Assert.Equal(0, result.MatchingFeedbackCount);
        Assert.Equal(0, result.Confidence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task One_sided_or_skewed_feedback_keeps_unsupported_articles_neutral(int negativeCount)
    {
        var examples = Enumerable.Range(1, 24).Select(index =>
            new FeedbackExample(Candidate($"story-{index}", string.Empty),
                index <= negativeCount ? FeedbackKind.NotInterested : FeedbackKind.Interested)).ToArray();
        var (repository, _) = Repository(examples);
        var result = await Ensemble(repository).ScoreAsync(Candidate("unrelated", "Unseen gardening subject"));

        Assert.Equal(0.5, result.Value);
        Assert.Equal(0, result.Confidence);
        Assert.Equal(RelevanceBand.Maybe, RelevanceBands.Classify(result.Value, result.Confidence));
    }

    [Fact]
    public async Task Single_and_batch_scoring_exclude_the_same_rated_story_and_its_duplicates()
    {
        var candidate = Candidate("original", "Amiga hardware preservation");
        var duplicate = candidate with { ExternalId = "syndicated", FeedSourceId = Guid.NewGuid() };
        var (repository, _) = Repository([
            new(candidate, FeedbackKind.VeryInterested), new(duplicate, FeedbackKind.VeryInterested)
        ]);
        var provider = Heuristic(repository);

        var single = await provider.ScoreAsync(candidate);
        var batch = (await provider.ScoreAsync([candidate]))[0];

        Assert.Equal(batch, single);
        Assert.Equal(0.5, single.Value);
        Assert.Equal(0, single.MatchingFeedbackCount);
        Assert.Equal(0, single.Confidence);
    }

    [Fact]
    public async Task Ensemble_loads_one_feedback_snapshot_and_rebuilds_after_a_vote_changes_or_is_undone()
    {
        var (repository, snapshot) = Repository(BalancedHistory());
        var provider = Ensemble(repository);
        var candidate = Candidate("new-story", "Amiga hardware");
        var original = await provider.ScoreAsync(candidate);
        Assert.Equal(1, snapshot.FeedbackReads);
        Assert.Equal(RelevanceBand.High, RelevanceBands.Classify(original.Value, original.Confidence));

        snapshot.Examples = snapshot.Examples.Select(e => e with { Kind = (FeedbackKind)(-(int)e.Kind) }).ToArray();
        var reversed = await provider.ScoreAsync(candidate);
        Assert.Equal(2, snapshot.FeedbackReads);
        Assert.Equal(RelevanceBand.Filtered, RelevanceBands.Classify(reversed.Value, reversed.Confidence));

        snapshot.Examples = [];
        var undone = await provider.ScoreAsync(candidate);
        Assert.Equal(3, snapshot.FeedbackReads);
        Assert.Equal(0.5, undone.Value);
        Assert.Equal(0, undone.Confidence);
    }

    private static IReadOnlyList<FeedbackExample> BalancedHistory(
        string positiveTopic = "Amiga hardware", string negativeTopic = "Celebrity fashion") => Enumerable.Range(1, 12)
        .SelectMany(index => new[]
        {
            new FeedbackExample(Candidate($"positive-{index}", $"{positiveTopic} {index}", "Weekly digest"), FeedbackKind.VeryInterested),
            new FeedbackExample(Candidate($"negative-{index}", $"{negativeTopic} {index}", "Weekly digest"), FeedbackKind.NeverThisTopic)
        }).ToArray();

    private static readonly Guid FeedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static ArticleCandidate Candidate(string id, string title, string? summary = null) =>
        new(id, title, $"https://example.test/{id}", summary, null, DateTimeOffset.UnixEpoch, FeedId);

    private static LocalPreferenceScoringProvider Heuristic(IFeedRepository repository)
    {
        var options = Options.Create(new ScoringOptions());
        return new(new KeywordScoringProvider(options), repository, options);
    }

    private static PrecisionEnsembleScoringProvider Ensemble(IFeedRepository repository) =>
        new(Heuristic(repository), repository, Options.Create(new ScoringOptions()));

    private static (IFeedRepository Repository, FeedbackSnapshot Snapshot) Repository(IReadOnlyList<FeedbackExample> examples)
    {
        var repository = DispatchProxy.Create<IFeedRepository, FeedbackSnapshot>();
        var snapshot = (FeedbackSnapshot)(object)repository;
        snapshot.Examples = examples;
        return (repository, snapshot);
    }

    public class FeedbackSnapshot : DispatchProxy
    {
        public IReadOnlyList<FeedbackExample> Examples { get; set; } = [];
        public int FeedbackReads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IFeedRepository.GetFeedbackExamplesAsync))
            {
                FeedbackReads++;
                return Task.FromResult(Examples);
            }
            if (targetMethod.Name == nameof(IFeedRepository.GetAvoidedTopicRulesAsync))
                return Task.FromResult<IReadOnlyList<AvoidedTopicRule>>([]);
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
