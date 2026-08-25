using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class PreferenceLearningOptions
{
    public double MaximumAdjustment { get; set; } = 0.35;
    public double EvidenceForFullConfidence { get; set; } = 6;
    public double MinimumFeatureAgreement { get; set; } = 0.25;
}

public sealed partial class LocalPreferenceScoringProvider(
    KeywordScoringProvider baselineProvider,
    IFeedRepository repository,
    IOptions<ScoringOptions> options) : IBatchScoringProvider
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "against", "all", "also", "and", "any", "are", "because", "been", "before",
        "being", "between", "both", "but", "can", "could", "did", "does", "doing", "don", "down", "each", "few",
        "for", "form", "from", "further", "had", "has", "have", "having", "here", "hers", "him", "his", "how", "into",
        "its", "itself", "just", "like", "more", "most", "new", "nor", "not", "now", "off", "once", "only",
        "other", "our", "ours", "out", "over", "own", "same", "she", "should", "some", "such", "than", "that",
        "the", "their", "theirs", "them", "then", "there", "these", "they", "this", "those", "through", "too",
        "under", "until", "using", "very", "was", "were", "what", "when", "where", "which", "while", "who",
        "whom", "why", "will", "with", "would", "you", "your", "yours"
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
            return new ScoreResult(baseline.Value, $"{baseline.Reason} No personal feedback is available yet.", baseline.Value, baseline.Reason,
                ConfidenceReason: "Low confidence: no personal feedback is available yet.");

        var candidateFeatures = Features(article);
        var exampleFeatures = examples.Select(example => (Example: example, Features: Features(example.Article))).ToList();
        var documentFrequency = exampleFeatures
            .SelectMany(item => item.Features.Keys.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(feature => feature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var evidence = new Dictionary<string, FeatureEvidence>(StringComparer.OrdinalIgnoreCase);
        var examplesByFeature = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        for (var exampleIndex = 0; exampleIndex < exampleFeatures.Count; exampleIndex++)
        {
            var item = exampleFeatures[exampleIndex];
            var sharedFeatures = candidateFeatures.Keys.Intersect(item.Features.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
            var independentFeatures = sharedFeatures
                .Where(feature => IsMeaningfulIndependentFeature(feature, documentFrequency.GetValueOrDefault(feature), examples.Count))
                .ToArray();
            if (independentFeatures.Length == 0) continue;
            var topicFeatures = independentFeatures
                .Where(feature => feature.StartsWith("topic:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var featuresForEvidence = topicFeatures.Length > 0
                ? topicFeatures
                : independentFeatures.Concat(sharedFeatures.Where(feature => feature.StartsWith("source:", StringComparison.OrdinalIgnoreCase)));
            var weightedFeatures = featuresForEvidence
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(feature =>
                {
                    var frequency = Math.Max(1, documentFrequency.GetValueOrDefault(feature));
                    var specificity = 1 + Math.Log((examples.Count + 1d) / (frequency + 1d));
                    var sourceScale = feature.StartsWith("source:", StringComparison.OrdinalIgnoreCase) ? 0.25 : 1;
                    var strength = candidateFeatures[feature].Weight * item.Features[feature].Weight * specificity * sourceScale;
                    return (Feature: feature, Strength: strength);
                })
                .Where(match => match.Strength > 0)
                .ToArray();
            var totalStrength = weightedFeatures.Sum(match => match.Strength);
            if (totalStrength <= 0) continue;
            var feedbackWeight = (int)item.Example.Kind;
            var voteMagnitude = Math.Abs(feedbackWeight);
            var voteDirection = Math.Sign(feedbackWeight);
            foreach (var match in weightedFeatures)
            {
                var contribution = voteDirection * voteMagnitude * (match.Strength / totalStrength);
                if (!evidence.TryGetValue(match.Feature, out var current)) current = new FeatureEvidence(candidateFeatures[match.Feature].Label, 0, 0);
                evidence[match.Feature] = current with { Signed = current.Signed + contribution, Absolute = current.Absolute + Math.Abs(contribution) };
                if (!examplesByFeature.TryGetValue(match.Feature, out var featureExamples))
                {
                    featureExamples = [];
                    examplesByFeature[match.Feature] = featureExamples;
                }
                featureExamples.Add(exampleIndex);
            }
        }

        if (evidence.Count == 0)
            return new ScoreResult(baseline.Value, $"{baseline.Reason} No learned interests matched this article.", baseline.Value, baseline.Reason,
                ConfidenceReason: "Low confidence: no independent learned interests matched this article.");

        var coherentEvidence = evidence
            .Where(item => item.Value.Absolute > 0 && Math.Abs(item.Value.Signed) / item.Value.Absolute >= _learning.MinimumFeatureAgreement)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (coherentEvidence.Count == 0)
            return new ScoreResult(baseline.Value, $"{baseline.Reason} Learned interests matched, but the feedback was too mixed to predict this article.", baseline.Value, baseline.Reason,
                ConfidenceReason: "Low confidence: matching feedback did not agree on a direction.");

        var coherentTopics = coherentEvidence
            .Where(item => item.Key.StartsWith("topic:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (coherentTopics.Count > 0) coherentEvidence = coherentTopics;

        var matchingExampleIndexes = coherentEvidence.Keys
            .SelectMany(feature => examplesByFeature.GetValueOrDefault(feature) ?? [])
            .Distinct()
            .ToArray();
        var matchingExamples = matchingExampleIndexes.Length;
        var totalSigned = coherentEvidence.Values.Sum(item => item.Signed);
        var totalEvidence = coherentEvidence.Values.Sum(item => item.Absolute);
        var polarity = totalEvidence == 0 ? 0 : totalSigned / totalEvidence;
        var agreement = totalEvidence == 0 ? 0 : Math.Abs(totalSigned) / totalEvidence;
        var support = Math.Min(1, totalEvidence / Math.Max(1, _learning.EvidenceForFullConfidence));
        var confidence = Math.Clamp(support * agreement, 0, 1);
        var adjustment = Math.Clamp(polarity * support * _learning.MaximumAdjustment, -_learning.MaximumAdjustment, _learning.MaximumAdjustment);
        var score = Math.Clamp(baseline.Value + adjustment, 0, 1);
        var strongest = coherentEvidence.Values.OrderByDescending(item => Math.Abs(item.Signed)).Take(3)
            .Select(item => $"{item.Label} {(item.Signed >= 0 ? "+" : string.Empty)}{item.Signed:0.00}").ToArray();
        var direction = adjustment >= 0 ? "+" : string.Empty;
        var reason = $"{baseline.Reason} Personal model {direction}{adjustment:0.00} from {string.Join(", ", strongest)} using {matchingExamples} matching feedback choice{(matchingExamples == 1 ? string.Empty : "s")}.";
        var confidenceLabel = confidence >= 0.75 ? "High" : confidence >= RelevanceBands.RequiredConfidence ? "Medium" : "Low";
        var confidenceReason = $"{confidenceLabel} confidence ({confidence:0.00}): {matchingExamples} meaningful matching feedback choice{(matchingExamples == 1 ? string.Empty : "s")}, {totalEvidence:0.0} weighted evidence, {agreement:P0} agreement.";
        return new ScoreResult(score, reason, baseline.Value, baseline.Reason, confidence, matchingExamples, confidenceReason);
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
        var title = WebUtility.HtmlDecode(article.Title);
        var summary = StripHtml(article.Summary);
        AddText(result, title, 1.5, includeBigrams: true);
        AddText(result, summary, 0.7, includeBigrams: true);
        AddKnownTopics(result, $"{title} {summary}");
        AddExact(result, article.Author, "author", 1.25);
        AddExact(result, article.FeedName, "source", 1.1);
        return result;
    }

    private static void AddText(IDictionary<string, Feature> features, string? value, double weight, bool includeBigrams)
    {
        var separated = AlphaNumericBoundary().Replace(value ?? string.Empty, " ");
        var words = Words().Matches(separated).Select(match => match.Value.ToLowerInvariant())
            .Where(word => (word.Length >= 3 || word.All(char.IsDigit)) && !StopWords.Contains(word)).Take(80).ToArray();
        foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
            if (word.Length >= 3) AddFeature(features, $"term:{word}", new Feature(word, weight));
        if (!includeBigrams) return;
        for (var index = 0; index + 1 < words.Length; index++)
        {
            var phrase = $"{words[index]} {words[index + 1]}";
            AddFeature(features, $"phrase:{phrase}", new Feature($"“{phrase}”", weight * 1.35));
        }
    }

    private static void AddKnownTopics(IDictionary<string, Feature> features, string value)
    {
        if (Gta6().IsMatch(value)) AddFeature(features, "topic:gta-6", new Feature("GTA 6", 2.5));
    }

    private static bool IsMeaningfulIndependentFeature(string feature, int documentFrequency, int exampleCount)
    {
        if (feature.StartsWith("topic:", StringComparison.OrdinalIgnoreCase) || feature.StartsWith("author:", StringComparison.OrdinalIgnoreCase)) return true;
        if (feature.StartsWith("source:", StringComparison.OrdinalIgnoreCase)) return false;
        var maximumShare = feature.StartsWith("phrase:", StringComparison.OrdinalIgnoreCase) ? 0.20 : 0.10;
        var maximumFrequency = Math.Max(8, (int)Math.Ceiling(exampleCount * maximumShare));
        return documentFrequency <= maximumFrequency;
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

    [GeneratedRegex(@"(?<=[\p{L}])(?=[\p{N}])|(?<=[\p{N}])(?=[\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex AlphaNumericBoundary();

    [GeneratedRegex(@"\b(?:gta\s*[- ]?\s*(?:6|vi)|grand\s+theft\s+auto\s*(?:6|vi))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gta6();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();

    private sealed record Feature(string Label, double Weight);
    private sealed record FeatureEvidence(string Label, double Signed, double Absolute);
}
