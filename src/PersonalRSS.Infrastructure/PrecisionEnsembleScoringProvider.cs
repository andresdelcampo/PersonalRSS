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
        return Combine(article, heuristic, await ModelContextAsync(feedback, cancellationToken));
    }

    public async Task<IReadOnlyList<ScoreResult>> ScoreAsync(
        IReadOnlyList<ArticleCandidate> articles,
        CancellationToken cancellationToken = default)
    {
        var heuristic = await heuristicProvider.ScoreAsync(articles, cancellationToken);
        var feedback = await repository.GetFeedbackExamplesAsync(null, cancellationToken);
        var context = await ModelContextAsync(feedback, cancellationToken);
        return articles.Select((article, index) => Combine(article, heuristic[index], context)).ToArray();
    }

    private async Task<PrecisionModelContext> ModelContextAsync(IReadOnlyList<FeedbackExample> feedback, CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(feedback);
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedContext is not null && string.Equals(_cachedFingerprint, fingerprint, StringComparison.Ordinal)) return _cachedContext;
            var context = PrecisionModelTrainer.Build(feedback, _learning);
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
        var heuristicBand = RelevanceBands.Classify(heuristic.Value, heuristic.Confidence);
        var positiveVote = context.PositiveCutoff.IsAvailable && prediction.Probability >= context.PositiveCutoff.Threshold;
        var negativeVote = context.NegativeCutoff.IsAvailable && prediction.Probability <= context.NegativeCutoff.Threshold;
        var features = prediction.StrongestFeatures.Count == 0
            ? "no reusable classifier features"
            : string.Join(", ", prediction.StrongestFeatures);
        var duplicateSummary = context.OriginalFeedbackCount == context.UniqueStoryCount
            ? $"{context.UniqueStoryCount} unique feedback stories"
            : $"{context.UniqueStoryCount} unique stories after collapsing {context.OriginalFeedbackCount - context.UniqueStoryCount} duplicate feedback item(s)";

        if (heuristicBand == RelevanceBand.High && positiveVote)
        {
            var score = Math.Max(RelevanceBands.HighScore, (heuristic.Value + prediction.Probability) / 2);
            var confidence = Math.Clamp(Math.Min(heuristic.Confidence, context.PositiveCutoff.Precision), RelevanceBands.RequiredConfidence, 1);
            var reason = $"{heuristic.Reason} Precision ensemble agrees: classifier {prediction.Probability:P0} positive from {features}; " +
                         $"cutoff {context.PositiveCutoff.Threshold:P0} achieved {context.PositiveCutoff.Precision:P0} cross-fitted precision on {context.PositiveCutoff.Predictions} held-out stories.";
            var confidenceReason = $"High-precision agreement ({confidence:0.00}) between the similarity scorer and local classifier, calibrated from {duplicateSummary}.";
            return heuristic with { Value = score, Reason = reason, Confidence = confidence, ConfidenceReason = confidenceReason };
        }

        if (heuristicBand == RelevanceBand.Filtered && negativeVote)
        {
            var score = Math.Min(RelevanceBands.FilteredScore, (heuristic.Value + prediction.Probability) / 2);
            var confidence = Math.Clamp(Math.Min(heuristic.Confidence, context.NegativeCutoff.Precision), RelevanceBands.RequiredConfidence, 1);
            var reason = $"{heuristic.Reason} Precision ensemble agrees: classifier {prediction.Probability:P0} negative from {features}; " +
                         $"cutoff {context.NegativeCutoff.Threshold:P0} achieved {context.NegativeCutoff.Precision:P0} cross-fitted precision on {context.NegativeCutoff.Predictions} held-out stories.";
            var confidenceReason = $"High-precision agreement ({confidence:0.00}) between the similarity scorer and local classifier, calibrated from {duplicateSummary}.";
            return heuristic with { Value = score, Reason = reason, Confidence = confidence, ConfidenceReason = confidenceReason };
        }

        var classifierVote = positiveVote ? "positive" : negativeVote ? "negative" : "uncertain";
        var maybeScore = Math.Clamp((heuristic.Value + prediction.Probability) / 2, RelevanceBands.FilteredScore + 0.01, RelevanceBands.HighScore - 0.01);
        var maybeConfidence = Math.Min(heuristic.Confidence, RelevanceBands.RequiredConfidence - 0.01);
        var maybeReason = $"{heuristic.Reason} Precision ensemble held this in Maybe: similarity vote {heuristicBand.ToString().ToLowerInvariant()}, " +
                          $"classifier {prediction.Probability:P0} ({classifierVote}; {features}). Both local models must agree before automatic High or Filtered.";
        var maybeConfidenceReason = $"Conservative disagreement ({maybeConfidence:0.00}); thresholds were cross-fitted from {duplicateSummary}.";
        return heuristic with { Value = maybeScore, Reason = maybeReason, Confidence = maybeConfidence, ConfidenceReason = maybeConfidenceReason };
    }
}
