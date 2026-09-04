using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class PrecisionEnsembleScoringProvider(
    LocalPreferenceScoringProvider heuristicProvider,
    IFeedRepository repository,
    IOptions<ScoringOptions> options) : IBatchScoringProvider
{
    private readonly PreferenceLearningOptions _learning = options.Value.PreferenceLearning;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private string? _cachedFingerprint;
    private PrecisionModelContext? _cachedContext;

    public string Name => "local-precision-ensemble";

    public async Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default)
    {
        var heuristic = await heuristicProvider.ScoreAsync(article, cancellationToken);
        var feedback = await repository.GetFeedbackExamplesAsync(null, cancellationToken);
        var result = Combine(article, heuristic, await ModelContextAsync(feedback, cancellationToken));
        return ApplyAvoidedTopicRules(article, result, await repository.GetAvoidedTopicRulesAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<ScoreResult>> ScoreAsync(
        IReadOnlyList<ArticleCandidate> articles,
        CancellationToken cancellationToken = default)
    {
        var heuristic = await heuristicProvider.ScoreAsync(articles, cancellationToken);
        var feedback = await repository.GetFeedbackExamplesAsync(null, cancellationToken);
        var context = await ModelContextAsync(feedback, cancellationToken);
        var rules = await repository.GetAvoidedTopicRulesAsync(cancellationToken);
        return articles.Select((article, index) => ApplyAvoidedTopicRules(article, Combine(article, heuristic[index], context), rules)).ToArray();
    }

    private static ScoreResult ApplyAvoidedTopicRules(ArticleCandidate article, ScoreResult result, IReadOnlyList<AvoidedTopicRule> rules)
    {
        var match = rules.OrderByDescending(rule => rule.NormalizedPhrase.Length)
            .FirstOrDefault(rule => AvoidedTopicText.Matches(article, rule));
        if (match is null) return result;
        return result with
        {
            Value = 0,
            Reason = $"Matched your explicit avoided topic \u201c{match.Phrase}\u201d. {result.Reason}",
            Confidence = 1,
            ConfidenceReason = "Certain automatic decision (1.00): an explicit avoided-topic rule matched this article. The rule overrides, but does not erase, the learned evidence shown for inspection."
        };
    }

    private async Task<PrecisionModelContext> ModelContextAsync(IReadOnlyList<FeedbackExample> feedback, CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(feedback);
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedContext is not null && string.Equals(_cachedFingerprint, fingerprint, StringComparison.Ordinal)) return _cachedContext;
            var heldOutHeuristics = await heuristicProvider.ScoreAsync(
                feedback.Select(item => item.Article).ToArray(), cancellationToken);
            var context = PrecisionModelTrainer.Build(feedback, heldOutHeuristics, _learning, cancellationToken);
            _cachedContext = context;
            _cachedFingerprint = fingerprint;
            return context;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private static string Fingerprint(IEnumerable<FeedbackExample> feedback)
    {
        var snapshot = string.Join('\n', feedback
            .OrderBy(item => DuplicateStoryClusterer.CandidateKey(item.Article), StringComparer.Ordinal)
            .Select(item => string.Join('\u001f',
                DuplicateStoryClusterer.CandidateKey(item.Article),
                ((int)item.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Article.Title,
                item.Article.Link,
                item.Article.Summary ?? string.Empty,
                item.Article.Author ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }

    private static ScoreResult Combine(ArticleCandidate article, ScoreResult heuristic, PrecisionModelContext context)
    {
        if (!context.IsReady) return heuristic;

        var prediction = context.Predict(article);
        var combinedProbability = PrecisionModelTrainer.CombinedProbability(heuristic.Value, prediction.Probability);
        var positiveVote = context.PositiveCutoff.IsAvailable && combinedProbability >= context.PositiveCutoff.Threshold;
        var negativeVote = context.NegativeCutoff.IsAvailable && combinedProbability <= context.NegativeCutoff.Threshold;
        var features = prediction.StrongestFeatures.Count == 0
            ? "no reusable classifier features"
            : string.Join(", ", prediction.StrongestFeatures);
        var duplicateSummary = context.OriginalFeedbackCount == context.UniqueStoryCount
            ? $"{context.UniqueStoryCount} unique feedback stories"
            : $"{context.UniqueStoryCount} unique stories after collapsing {context.OriginalFeedbackCount - context.UniqueStoryCount} duplicate feedback item(s)";

        if (positiveVote)
        {
            var score = Math.Max(RelevanceBands.HighScore, combinedProbability);
            var confidence = Math.Clamp(context.PositiveCutoff.Precision, RelevanceBands.RequiredConfidence, 1);
            var reason = $"{heuristic.Reason} Calibrated combined model: similarity {heuristic.Value:P0}, classifier {prediction.Probability:P0} positive from {features}; " +
                         $"combined cutoff {context.PositiveCutoff.Threshold:P0} achieved {context.PositiveCutoff.Precision:P0} cross-fitted precision on {context.PositiveCutoff.Predictions} held-out stories.";
            var confidenceReason = $"Calibrated positive decision ({confidence:0.00}) from the combined local prediction, trained on {duplicateSummary}.";
            return heuristic with { Value = score, Reason = reason, Confidence = confidence, ConfidenceReason = confidenceReason };
        }

        if (negativeVote)
        {
            var score = Math.Min(RelevanceBands.FilteredScore, combinedProbability);
            var confidence = Math.Clamp(context.NegativeCutoff.Precision, RelevanceBands.RequiredConfidence, 1);
            var reason = $"{heuristic.Reason} Calibrated combined model: similarity {heuristic.Value:P0}, classifier {prediction.Probability:P0} negative from {features}; " +
                         $"combined cutoff {context.NegativeCutoff.Threshold:P0} achieved {context.NegativeCutoff.Precision:P0} cross-fitted precision on {context.NegativeCutoff.Predictions} held-out stories.";
            var confidenceReason = $"Calibrated negative decision ({confidence:0.00}) from the combined local prediction, trained on {duplicateSummary}.";
            return heuristic with { Value = score, Reason = reason, Confidence = confidence, ConfidenceReason = confidenceReason };
        }

        var maybeScore = Math.Clamp(combinedProbability, RelevanceBands.FilteredScore + 0.01, RelevanceBands.HighScore - 0.01);
        var maybeConfidence = Math.Min(heuristic.Confidence, RelevanceBands.RequiredConfidence - 0.01);
        var maybeReason = $"{heuristic.Reason} Combined model held this in Maybe: similarity {heuristic.Value:P0}, " +
                          $"classifier {prediction.Probability:P0} from {features}, combined {combinedProbability:P0}; it did not reach either calibrated precision cutoff.";
        var maybeConfidenceReason = $"Uncertain combined prediction ({maybeConfidence:0.00}); decision cutoffs were cross-fitted from {duplicateSummary}.";
        return heuristic with { Value = maybeScore, Reason = maybeReason, Confidence = maybeConfidence, ConfidenceReason = maybeConfidenceReason };
    }
}
