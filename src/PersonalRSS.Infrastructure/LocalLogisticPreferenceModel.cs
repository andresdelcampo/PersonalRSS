using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

internal sealed record CalibratedCutoff(double Threshold, double Precision, int Predictions)
{
    public bool IsAvailable => Predictions > 0;
}

internal sealed record LogisticPrediction(double Probability, IReadOnlyList<string> StrongestFeatures);

internal sealed class PrecisionModelContext
{
    private readonly LocalLogisticPreferenceModel? _globalModel;
    private readonly IReadOnlyDictionary<(Guid FeedSourceId, string ExternalId), double> _crossFittedProbabilities;

    public PrecisionModelContext(
        LocalLogisticPreferenceModel? globalModel,
        IReadOnlyDictionary<(Guid FeedSourceId, string ExternalId), double> crossFittedProbabilities,
        CalibratedCutoff positiveCutoff,
        CalibratedCutoff negativeCutoff,
        int originalFeedbackCount,
        int uniqueStoryCount)
    {
        _globalModel = globalModel;
        _crossFittedProbabilities = crossFittedProbabilities;
        PositiveCutoff = positiveCutoff;
        NegativeCutoff = negativeCutoff;
        OriginalFeedbackCount = originalFeedbackCount;
        UniqueStoryCount = uniqueStoryCount;
    }

    public bool IsReady => _globalModel is not null;
    public CalibratedCutoff PositiveCutoff { get; }
    public CalibratedCutoff NegativeCutoff { get; }
    public int OriginalFeedbackCount { get; }
    public int UniqueStoryCount { get; }

    public LogisticPrediction Predict(ArticleCandidate article)
    {
        if (article.FeedSourceId.HasValue &&
            _crossFittedProbabilities.TryGetValue((article.FeedSourceId.Value, article.ExternalId), out var probability))
            return new LogisticPrediction(probability, ["held-out feedback story"]);
        return _globalModel?.Predict(article) ?? new LogisticPrediction(0.5, []);
    }

    public static PrecisionModelContext NotReady(int originalFeedbackCount, int uniqueStoryCount) =>
        new(null, new Dictionary<(Guid, string), double>(), new CalibratedCutoff(1, 0, 0), new CalibratedCutoff(0, 0, 0), originalFeedbackCount, uniqueStoryCount);
}

internal static class PrecisionModelTrainer
{
    public static PrecisionModelContext Build(
        IReadOnlyList<FeedbackExample> feedback,
        IReadOnlyList<ScoreResult> heldOutHeuristics,
        PreferenceLearningOptions options,
        CancellationToken cancellationToken = default)
    {
        if (feedback.Count != heldOutHeuristics.Count)
            throw new ArgumentException("Feedback and held-out heuristic predictions must have the same length.");
        var clusters = DuplicateStoryClusterer.Collapse(feedback, cancellationToken);
        if (clusters.Count < options.MinimumCalibrationExamples)
            return PrecisionModelContext.NotReady(feedback.Count, clusters.Count);
        var heuristicByCandidate = feedback.Select((item, index) =>
                (Key: DuplicateStoryClusterer.CandidateKey(item.Article), Score: heldOutHeuristics[index]))
            .ToDictionary(item => item.Key, item => item.Score, StringComparer.Ordinal);

        var folds = Math.Clamp(options.CalibrationFolds, 2, Math.Min(10, clusters.Count));
        var crossFitted = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var fold = 0; fold < folds; fold++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var training = clusters.Where(cluster => Fold(cluster.StableKey, folds) != fold).ToArray();
            var validation = clusters.Where(cluster => Fold(cluster.StableKey, folds) == fold).ToArray();
            if (validation.Length == 0) continue;
            var model = LocalLogisticPreferenceModel.Train(training, options, cancellationToken);
            foreach (var cluster in validation) crossFitted[cluster.StableKey] = model.Predict(cluster.Representative.Article).Probability;
        }

        var evaluated = clusters
            .Where(cluster => crossFitted.ContainsKey(cluster.StableKey) &&
                              heuristicByCandidate.ContainsKey(DuplicateStoryClusterer.CandidateKey(cluster.Representative.Article)))
            .Select(cluster => new CalibrationExample(
                CombinedProbability(
                    heuristicByCandidate[DuplicateStoryClusterer.CandidateKey(cluster.Representative.Article)].Value,
                    crossFitted[cluster.StableKey]),
                (int)cluster.Representative.Kind > 0,
                cluster))
            .ToArray();
        var positiveCutoff = Calibrate(evaluated, positive: true, options.TargetPrecision, options.MinimumCalibratedPredictions);
        var negativeCutoff = Calibrate(evaluated, positive: false, options.TargetPrecision, options.MinimumCalibratedPredictions);
        var memberPredictions = new Dictionary<(Guid FeedSourceId, string ExternalId), double>();
        foreach (var item in evaluated)
        {
            foreach (var member in item.Cluster.Members.Where(member => member.Article.FeedSourceId.HasValue))
                memberPredictions[(member.Article.FeedSourceId!.Value, member.Article.ExternalId)] =
                    crossFitted[item.Cluster.StableKey];
        }
        return new PrecisionModelContext(
            LocalLogisticPreferenceModel.Train(clusters, options, cancellationToken),
            memberPredictions,
            positiveCutoff,
            negativeCutoff,
            feedback.Count,
            clusters.Count);
    }

    public static double CombinedProbability(double heuristicProbability, double classifierProbability) =>
        Math.Clamp((heuristicProbability + classifierProbability) / 2, 0, 1);

    private static CalibratedCutoff Calibrate(
        IReadOnlyList<CalibrationExample> examples,
        bool positive,
        double targetPrecision,
        int minimumPredictions)
    {
        var eligible = examples.Where(example => positive ? example.Probability >= 0.5 : example.Probability <= 0.5);
        var ordered = positive
            ? eligible.GroupBy(example => example.Probability).OrderByDescending(group => group.Key).ToArray()
            : eligible.GroupBy(example => example.Probability).OrderBy(group => group.Key).ToArray();
        CalibratedCutoff? best = null;
        var correct = 0;
        var count = 0;
        foreach (var group in ordered)
        {
            correct += group.Count(example => example.IsPositive == positive);
            count += group.Count();
            if (count < minimumPredictions) continue;
            var precision = correct / (double)count;
            if (precision + 1e-9 < targetPrecision) continue;
            best = new CalibratedCutoff(group.Key, precision, count);
        }
        return best ?? new CalibratedCutoff(positive ? 1 : 0, 0, 0);
    }

    private static int Fold(string stableKey, int folds) => (int)(DuplicateStoryClusterer.StableHash(stableKey) % (ulong)folds);

    private sealed record CalibrationExample(double Probability, bool IsPositive, StoryFeedbackCluster Cluster);
}

internal sealed class LocalLogisticPreferenceModel
{
    private readonly IReadOnlyDictionary<string, int> _featureIndexes;
    private readonly string[] _featureNames;
    private readonly double[] _idf;
    private readonly double[] _weights;
    private readonly double _bias;

    private LocalLogisticPreferenceModel(IReadOnlyDictionary<string, int> featureIndexes, string[] featureNames, double[] idf, double[] weights, double bias)
    {
        _featureIndexes = featureIndexes;
        _featureNames = featureNames;
        _idf = idf;
        _weights = weights;
        _bias = bias;
    }

    public static LocalLogisticPreferenceModel Train(
        IReadOnlyList<StoryFeedbackCluster> clusters,
        PreferenceLearningOptions options,
        CancellationToken cancellationToken = default)
    {
        var raw = clusters.Select(cluster => LocalPreferenceScoringProvider.LogisticFeatures(cluster.Representative.Article)).ToArray();
        var targets = clusters.Select(cluster => (int)cluster.Representative.Kind > 0 ? 1d : 0d).ToArray();
        var magnitudes = clusters.Select(cluster => (double)Math.Abs((int)cluster.Representative.Kind)).ToArray();
        var positiveWeight = targets.Select((target, index) => target > 0.5 ? magnitudes[index] : 0).Sum();
        var negativeWeight = targets.Select((target, index) => target < 0.5 ? magnitudes[index] : 0).Sum();
        var documentFrequency = raw.SelectMany(features => features.Keys.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(feature => feature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var classEvidence = raw.SelectMany((features, index) => features.Keys.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(feature => new { Feature = feature, Target = targets[index], Weight = magnitudes[index] }))
            .GroupBy(item => item.Feature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new FeatureClassEvidence(
                    group.Where(item => item.Target > 0.5).Sum(item => item.Weight),
                    group.Where(item => item.Target < 0.5).Sum(item => item.Weight)),
                StringComparer.OrdinalIgnoreCase);
        var maximumFrequency = Math.Max(5, (int)Math.Ceiling(clusters.Count * 0.35));
        var vocabulary = documentFrequency
            .Where(item => item.Value <= maximumFrequency && (item.Value >= 2 || item.Key.StartsWith("topic:", StringComparison.OrdinalIgnoreCase)))
            .Where(item => IsDiscriminative(classEvidence[item.Key], positiveWeight, negativeWeight, options.MinimumFeatureLogOdds))
            .Select(item => item.Key)
            .OrderBy(feature => feature, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var indexes = vocabulary.Select((feature, index) => (feature, index))
            .ToDictionary(item => item.feature, item => item.index, StringComparer.OrdinalIgnoreCase);
        var idf = vocabulary.Select(feature => 1 + Math.Log((clusters.Count + 1d) / (documentFrequency[feature] + 1d))).ToArray();
        var samples = raw.Select((features, index) => new TrainingSample(
            Vectorize(features, indexes, idf),
            targets[index],
            BalancedSampleWeight(targets[index], magnitudes[index], positiveWeight, negativeWeight))).ToArray();
        var bias = 0d;
        var weights = new double[vocabulary.Length];
        var gradient = new double[vocabulary.Length];
        var totalSampleWeight = Math.Max(1, samples.Sum(sample => sample.SampleWeight));
        for (var epoch = 0; epoch < 180; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(gradient);
            var biasGradient = 0d;
            foreach (var sample in samples)
            {
                var prediction = Sigmoid(bias + Dot(weights, sample.Values));
                var error = (prediction - sample.Target) * sample.SampleWeight;
                biasGradient += error;
                foreach (var value in sample.Values) gradient[value.Index] += error * value.Value;
            }
            var step = 0.8 / Math.Sqrt(1 + epoch / 30d);
            bias -= step * biasGradient / totalSampleWeight;
            for (var index = 0; index < weights.Length; index++)
                weights[index] -= step * (gradient[index] / totalSampleWeight + 0.005 * weights[index]);
        }
        return new LocalLogisticPreferenceModel(indexes, vocabulary, idf, weights, bias);
    }

    private static bool IsDiscriminative(
        FeatureClassEvidence evidence,
        double positiveWeight,
        double negativeWeight,
        double minimumLogOdds)
    {
        if (positiveWeight <= 0 || negativeWeight <= 0 || minimumLogOdds <= 0) return true;
        var positiveRate = (evidence.PositiveWeight + 1) / (positiveWeight + 2);
        var negativeRate = (evidence.NegativeWeight + 1) / (negativeWeight + 2);
        return Math.Abs(Math.Log(positiveRate / negativeRate)) >= minimumLogOdds;
    }

    private static double BalancedSampleWeight(
        double target,
        double magnitude,
        double positiveWeight,
        double negativeWeight)
    {
        if (positiveWeight <= 0 || negativeWeight <= 0) return magnitude;
        var targetClassWeight = (positiveWeight + negativeWeight) / 2;
        var classWeight = target > 0.5 ? positiveWeight : negativeWeight;
        return magnitude * targetClassWeight / classWeight;
    }

    public LogisticPrediction Predict(ArticleCandidate article)
    {
        var values = Vectorize(LocalPreferenceScoringProvider.LogisticFeatures(article), _featureIndexes, _idf);
        var probability = Sigmoid(_bias + Dot(_weights, values));
        var strongest = values
            .Select(value => (Feature: _featureNames[value.Index], Contribution: _weights[value.Index] * value.Value))
            .Where(item => Math.Abs(item.Contribution) >= 0.01)
            .OrderByDescending(item => Math.Abs(item.Contribution))
            .Take(3)
            .Select(item => $"{Label(item.Feature)} {(item.Contribution >= 0 ? "+" : string.Empty)}{item.Contribution:0.00}")
            .ToArray();
        return new LogisticPrediction(probability, strongest);
    }

    private static FeatureValue[] Vectorize(
        IReadOnlyDictionary<string, double> features,
        IReadOnlyDictionary<string, int> indexes,
        IReadOnlyList<double> idf)
    {
        var values = features
            .Where(item => indexes.ContainsKey(item.Key))
            .Select(item => new FeatureValue(indexes[item.Key], item.Value * idf[indexes[item.Key]]))
            .ToArray();
        var length = Math.Sqrt(values.Sum(value => value.Value * value.Value));
        return length <= 0 ? values : values.Select(value => value with { Value = value.Value / length }).ToArray();
    }

    private static double Dot(IReadOnlyList<double> weights, IReadOnlyList<FeatureValue> values) =>
        values.Sum(value => weights[value.Index] * value.Value);

    private static double Sigmoid(double value)
    {
        value = Math.Clamp(value, -30, 30);
        return 1 / (1 + Math.Exp(-value));
    }

    private static string Label(string feature)
    {
        var separator = feature.IndexOf(':');
        return separator < 0 ? feature : feature[(separator + 1)..];
    }

    private sealed record TrainingSample(FeatureValue[] Values, double Target, double SampleWeight);
    private sealed record FeatureValue(int Index, double Value);
    private sealed record FeatureClassEvidence(double PositiveWeight, double NegativeWeight);
}
