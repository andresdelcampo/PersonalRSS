using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class PreferenceLearningOptions
{
    public double MaximumAdjustment { get; set; } = 0.35;
    public double EvidenceForFullConfidence { get; set; } = 8;
}

public sealed partial class LocalPreferenceScoringProvider(
    KeywordScoringProvider baselineProvider,
    IFeedRepository repository,
    IOptions<ScoringOptions> options) : IBatchScoringProvider
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "because", "been", "before", "being", "between", "could", "from",
        "have", "into", "more", "most", "other", "over", "than", "that", "their", "there", "these", "they",
        "this", "through", "under", "very", "what", "when", "where", "which", "while", "with", "would", "your"
    };

    private readonly PreferenceLearningOptions _learning = options.Value.PreferenceLearning;
    public string Name => "local-preference-model";

    public async Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default)
    {
        var examples = await repository.GetFeedbackExamplesAsync(StableId(article), cancellationToken);
        return await ScoreAsync(article, examples, cancellationToken);
    }

    public async Task<IReadOnlyList<ScoreResult>> ScoreAsync(IReadOnlyList<ArticleCandidate> articles, CancellationToken cancellationToken = default)
    {
        var examples = await repository.GetFeedbackExamplesAsync(null, cancellationToken);
        var results = new List<ScoreResult>(articles.Count);
        foreach (var article in articles)
        {
            var articleId = StableId(article);
            var applicableExamples = articleId is null
                ? examples
                : examples.Where(example => example.Article.FeedSourceId != article.FeedSourceId || example.Article.ExternalId != article.ExternalId).ToList();
            results.Add(await ScoreAsync(article, applicableExamples, cancellationToken));
        }
        return results;
    }

    private async Task<ScoreResult> ScoreAsync(ArticleCandidate article, IReadOnlyList<FeedbackExample> examples, CancellationToken cancellationToken)
    {
        var baseline = await baselineProvider.ScoreAsync(article, cancellationToken);
        if (examples.Count == 0)
            return new ScoreResult(baseline.Value, $"{baseline.Reason} No personal feedback is available yet.", baseline.Value, baseline.Reason);

        var candidateFeatures = Features(article);
        var evidence = new Dictionary<string, FeatureEvidence>(StringComparer.OrdinalIgnoreCase);
        var matchingExamples = 0;
        foreach (var example in examples)
        {
            var exampleFeatures = Features(example.Article);
            var feedbackWeight = (int)example.Kind;
            var sharedFeatures = candidateFeatures.Keys.Intersect(exampleFeatures.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
            if (sharedFeatures.Length > 0) matchingExamples++;
            foreach (var feature in sharedFeatures)
            {
                var contribution = candidateFeatures[feature].Weight * exampleFeatures[feature].Weight * feedbackWeight;
                if (!evidence.TryGetValue(feature, out var current)) current = new FeatureEvidence(candidateFeatures[feature].Label, 0, 0);
                evidence[feature] = current with { Signed = current.Signed + contribution, Absolute = current.Absolute + Math.Abs(contribution) };
            }
        }

        if (evidence.Count == 0)
            return new ScoreResult(baseline.Value, $"{baseline.Reason} No learned interests matched this article.", baseline.Value, baseline.Reason);

        var totalSigned = evidence.Values.Sum(item => item.Signed);
        var totalEvidence = evidence.Values.Sum(item => item.Absolute);
        var polarity = totalEvidence == 0 ? 0 : totalSigned / totalEvidence;
        var confidence = Math.Min(1, totalEvidence / Math.Max(1, _learning.EvidenceForFullConfidence));
        var adjustment = Math.Clamp(polarity * confidence * _learning.MaximumAdjustment, -_learning.MaximumAdjustment, _learning.MaximumAdjustment);
        var score = Math.Clamp(baseline.Value + adjustment, 0, 1);
        var strongest = evidence.Values.OrderByDescending(item => Math.Abs(item.Signed)).Take(3).Select(item => item.Label).ToArray();
        var direction = adjustment >= 0 ? "+" : string.Empty;
        var reason = $"{baseline.Reason} Personal model {direction}{adjustment:0.00} from {string.Join(", ", strongest)} using {matchingExamples} matching feedback choice{(matchingExamples == 1 ? string.Empty : "s")}.";
        return new ScoreResult(score, reason, baseline.Value, baseline.Reason);
    }

    private static Guid? StableId(ArticleCandidate article)
    {
        if (article.FeedSourceId is null) return null;
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{article.FeedSourceId:N}:{article.ExternalId}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static Dictionary<string, Feature> Features(ArticleCandidate article)
    {
        var result = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
        AddText(result, WebUtility.HtmlDecode(article.Title), 1.5, includeBigrams: true);
        AddText(result, StripHtml(article.Summary), 0.7, includeBigrams: true);
        AddExact(result, article.Author, "author", 1.25);
        AddExact(result, article.FeedName, "source", 1.1);
        return result;
    }

    private static void AddText(IDictionary<string, Feature> features, string? value, double weight, bool includeBigrams)
    {
        var words = Words().Matches(value ?? string.Empty).Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length >= 3 && !StopWords.Contains(word)).Distinct().Take(80).ToArray();
        foreach (var word in words) AddFeature(features, $"term:{word}", new Feature(word, weight));
        if (!includeBigrams) return;
        for (var index = 0; index + 1 < words.Length; index++)
        {
            var phrase = $"{words[index]} {words[index + 1]}";
            AddFeature(features, $"phrase:{phrase}", new Feature($"“{phrase}”", weight * 1.35));
        }
    }

    private static void AddExact(IDictionary<string, Feature> features, string? value, string prefix, double weight)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = Regex.Replace(WebUtility.HtmlDecode(value).Trim().ToLowerInvariant(), "\\s+", " ");
        features[$"{prefix}:{normalized}"] = new Feature($"{prefix} {value.Trim()}", weight);
    }

    private static void AddFeature(IDictionary<string, Feature> features, string key, Feature feature)
    {
        if (!features.TryGetValue(key, out var existing) || feature.Weight > existing.Weight) features[key] = feature;
    }

    private static string? StripHtml(string? value) => value is null ? null : HtmlTags().Replace(WebUtility.HtmlDecode(value), " ");

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}+#.-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();

    private sealed record Feature(string Label, double Weight);
    private sealed record FeatureEvidence(string Label, double Signed, double Absolute);
}
