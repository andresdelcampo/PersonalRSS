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
    public double MinimumExampleSimilarity { get; set; } = 0.40;
    public int MinimumCalibrationExamples { get; set; } = 20;
    public int CalibrationFolds { get; set; } = 5;
    public int MinimumCalibratedPredictions { get; set; } = 8;
    public double TargetPrecision { get; set; } = 0.85;
}

public sealed partial class LocalPreferenceScoringProvider(
    KeywordScoringProvider baselineProvider,
    IFeedRepository repository,
    IOptions<ScoringOptions> options) : IBatchScoringProvider
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "against", "all", "also", "and", "any", "app", "apps", "are", "article", "articles", "because", "been", "before", "better", "blog",
        "being", "between", "both", "but", "can", "comment", "comments", "could", "did", "does", "doing", "don", "down", "due", "each", "few", "first",
        "for", "form", "from", "further", "get", "had", "has", "have", "having", "here", "hers", "him", "his", "how", "important", "into",
        "highest", "its", "itself", "just", "like", "model", "models", "more", "most", "new", "news", "nor", "not", "now", "off", "once", "only",
        "other", "our", "ours", "out", "over", "own", "platform", "point", "points", "release", "releases", "remain", "report", "reports", "same", "she", "should", "some", "status", "such", "than", "that",
        "the", "their", "theirs", "them", "then", "there", "these", "they", "this", "those", "through", "today", "too",
        "under", "until", "update", "updates", "using", "very", "was", "week", "were", "what", "when", "where", "which", "while", "who", "works",
        "whom", "why", "will", "with", "would", "you", "your", "yours"
    };

    private readonly PreferenceLearningOptions _learning = options.Value.PreferenceLearning;
    public string Name => "local-preference-model";

    public async Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default)
    {
        var examples = await repository.GetFeedbackExamplesAsync(StableId(article), cancellationToken);
        return await ScoreAsync(article, Prepare(examples), null, cancellationToken);
    }

    public async Task<IReadOnlyList<ScoreResult>> ScoreAsync(IReadOnlyList<ArticleCandidate> articles, CancellationToken cancellationToken = default)
    {
        var examples = await repository.GetFeedbackExamplesAsync(null, cancellationToken);
        var context = Prepare(examples);
        var feedbackIndexes = context.Examples.SelectMany((item, index) => item.MemberKeys.Select(key => (Key: key, Index: index)))
            .ToDictionary(item => item.Key, item => item.Index);
        var results = new List<ScoreResult>(articles.Count);
        foreach (var article in articles)
        {
            int? excludedIndex = null;
            if (article.FeedSourceId.HasValue && feedbackIndexes.TryGetValue((article.FeedSourceId.Value, article.ExternalId), out var index))
                excludedIndex = index;
            results.Add(await ScoreAsync(article, context, excludedIndex, cancellationToken));
        }
        return results;
    }

    private async Task<ScoreResult> ScoreAsync(ArticleCandidate article, LearningContext context, int? excludedIndex, CancellationToken cancellationToken)
    {
        var baseline = await baselineProvider.ScoreAsync(article, cancellationToken);
        var exampleCount = context.Examples.Count - (excludedIndex.HasValue ? 1 : 0);
        if (exampleCount == 0)
            return new ScoreResult(baseline.Value, $"{baseline.Reason} No personal feedback is available yet.", baseline.Value, baseline.Reason,
                ConfidenceReason: "Low confidence: no personal feedback is available yet.");

        var candidateFeatures = Features(article);
        var evidence = new Dictionary<string, FeatureEvidence>(StringComparer.OrdinalIgnoreCase);
        var examplesByFeature = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        for (var exampleIndex = 0; exampleIndex < context.Examples.Count; exampleIndex++)
        {
            if (exampleIndex == excludedIndex) continue;
            var item = context.Examples[exampleIndex];
            var sharedFeatures = candidateFeatures.Keys.Intersect(item.Features.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
            var documentFrequency = sharedFeatures.ToDictionary(feature => feature,
                feature => AdjustedDocumentFrequency(feature, context, excludedIndex), StringComparer.OrdinalIgnoreCase);
            var meaningfulFeatures = sharedFeatures
                .Where(feature => IsMeaningfulIndependentFeature(feature, documentFrequency.GetValueOrDefault(feature), exampleCount))
                .ToArray();
            var selectedFeatures = SelectEvidenceFeatures(meaningfulFeatures, documentFrequency);
            if (selectedFeatures.Length == 0) continue;
            var weightedFeatures = selectedFeatures
                .Select(feature =>
                {
                    var frequency = Math.Max(1, documentFrequency.GetValueOrDefault(feature));
                    var specificity = 1 + Math.Log((exampleCount + 1d) / (frequency + 1d));
                    var strength = candidateFeatures[feature].Weight * item.Features[feature].Weight * specificity;
                    return (Feature: feature, Strength: strength);
                })
                .Where(match => match.Strength > 0)
                .ToArray();
            var totalStrength = weightedFeatures.Sum(match => match.Strength);
            if (totalStrength <= 0) continue;
            var similarity = ExampleSimilarity(selectedFeatures, totalStrength, documentFrequency);
            if (similarity < _learning.MinimumExampleSimilarity) continue;
            var feedbackWeight = (int)item.Example.Kind;
            var voteMagnitude = Math.Abs(feedbackWeight);
            var voteDirection = Math.Sign(feedbackWeight);
            foreach (var match in weightedFeatures)
            {
                var contribution = voteDirection * voteMagnitude * similarity * (match.Strength / totalStrength);
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

    private static LearningContext Prepare(IReadOnlyList<FeedbackExample> examples)
    {
        var prepared = DuplicateStoryClusterer.Collapse(examples)
            .Select(cluster => new PreparedFeedbackExample(
                cluster.Representative,
                Features(cluster.Representative.Article),
                cluster.Members.Where(member => member.Article.FeedSourceId.HasValue)
                    .Select(member => (member.Article.FeedSourceId!.Value, member.Article.ExternalId))
                    .ToHashSet()))
            .ToList();
        var documentFrequency = prepared.SelectMany(item => item.Features.Keys.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(feature => feature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new LearningContext(prepared, documentFrequency, examples.Count);
    }

    private static int AdjustedDocumentFrequency(string feature, LearningContext context, int? excludedIndex)
    {
        var frequency = context.DocumentFrequency.GetValueOrDefault(feature);
        if (excludedIndex.HasValue && context.Examples[excludedIndex.Value].Features.ContainsKey(feature)) frequency--;
        return Math.Max(0, frequency);
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
        return result;
    }

    internal static IReadOnlyDictionary<string, double> LogisticFeatures(ArticleCandidate article) =>
        Features(article).ToDictionary(item => item.Key, item => item.Value.Weight, StringComparer.OrdinalIgnoreCase);

    private static void AddText(IDictionary<string, Feature> features, string? value, double weight, bool includeBigrams)
    {
        var separated = AlphaNumericBoundary().Replace(value ?? string.Empty, " ");
        var words = Words().Matches(separated).Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length >= 3 && !word.All(char.IsDigit) && !StopWords.Contains(word)).Take(80).ToArray();
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
        var isPhrase = feature.StartsWith("phrase:", StringComparison.OrdinalIgnoreCase);
        var maximumShare = isPhrase ? 0.15 : 0.05;
        var minimumFrequencyAllowance = isPhrase ? 5 : 6;
        var maximumFrequency = Math.Max(minimumFrequencyAllowance, (int)Math.Ceiling(exampleCount * maximumShare));
        return documentFrequency <= maximumFrequency;
    }

    private static string[] SelectEvidenceFeatures(IReadOnlyCollection<string> features, IReadOnlyDictionary<string, int> documentFrequency)
    {
        var topics = features.Where(feature => feature.StartsWith("topic:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (topics.Length > 0) return topics;
        var phrases = features.Where(feature => feature.StartsWith("phrase:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (phrases.Length > 0) return phrases;
        var authors = features.Where(feature => feature.StartsWith("author:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (authors.Length > 0) return authors;
        var terms = features.Where(feature => feature.StartsWith("term:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (terms.Length >= 2) return terms;
        if (terms.Length == 1)
        {
            var value = terms[0]["term:".Length..];
            if (value.Length >= 6 && documentFrequency.GetValueOrDefault(terms[0]) >= 2) return terms;
        }
        return [];
    }

    private static double ExampleSimilarity(IReadOnlyCollection<string> features, double totalStrength, IReadOnlyDictionary<string, int> documentFrequency)
    {
        if (features.Any(feature => feature.StartsWith("topic:", StringComparison.OrdinalIgnoreCase))) return 1;
        if (features.Any(feature => feature.StartsWith("author:", StringComparison.OrdinalIgnoreCase))) return 0.8;
        if (features.Any(feature => feature.StartsWith("phrase:", StringComparison.OrdinalIgnoreCase)))
            return Math.Clamp(1 - Math.Exp(-totalStrength / 4), 0, 0.9);
        if (features.Count >= 2) return Math.Clamp(1 - Math.Exp(-totalStrength / 6), 0, 0.85);
        var repeatedExamples = documentFrequency.GetValueOrDefault(features.Single());
        return Math.Clamp(0.45 + 0.05 * Math.Min(3, repeatedExamples - 1), 0, 0.60);
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

    private static string? StripHtml(string? value)
    {
        if (value is null) return null;
        var text = HtmlTags().Replace(WebUtility.HtmlDecode(value), " ");
        text = FeedMetadata().Replace(text, " ");
        return Urls().Replace(text, " ");
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}+#.-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex(@"(?<=[\p{L}])(?=[\p{N}])|(?<=[\p{N}])(?=[\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex AlphaNumericBoundary();

    [GeneratedRegex(@"\b(?:gta\s*[- ]?\s*(?:6|vi)|grand\s+theft\s+auto\s*(?:6|vi))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gta6();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();

    [GeneratedRegex(@"(?:article|comments?)\s+url\s*:\s*\S+|points?\s*:\s*\d+|#\s*comments?\s*:\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedMetadata();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Urls();

    private sealed record Feature(string Label, double Weight);
    private sealed record FeatureEvidence(string Label, double Signed, double Absolute);
    private sealed record PreparedFeedbackExample(
        FeedbackExample Example,
        Dictionary<string, Feature> Features,
        IReadOnlySet<(Guid FeedSourceId, string ExternalId)> MemberKeys);
    private sealed record LearningContext(
        IReadOnlyList<PreparedFeedbackExample> Examples,
        IReadOnlyDictionary<string, int> DocumentFrequency,
        int OriginalFeedbackCount);
}
