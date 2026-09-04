using System.ComponentModel.DataAnnotations.Schema;

namespace PersonalRSS.Core;

public sealed class FeedSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public required string Url { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class Article
{
    public Guid Id { get; set; }
    public Guid FeedSourceId { get; set; }
    public FeedSource? FeedSource { get; set; }
    public required string ExternalId { get; set; }
    public required string Title { get; set; }
    public required string Link { get; set; }
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
    public bool IsUnreadPinned { get; set; }
    public double BaselineScore { get; set; } = 0.5;
    public string? BaselineScoreReason { get; set; }
    public double AutomaticScore { get; set; } = 0.5;
    public string? AutomaticScoreReason { get; set; }
    public double AutomaticConfidence { get; set; }
    public int MatchingFeedbackCount { get; set; }
    public double PositiveEvidence { get; set; }
    public double NegativeEvidence { get; set; }
    public string? ConfidenceReason { get; set; }
    public double Score { get; set; }
    public string? ScoreReason { get; set; }
    [NotMapped]
    public FeedbackKind? ActiveFeedback { get; set; }
    [NotMapped]
    public bool IsUnread { get; set; }
    [NotMapped]
    public double EvidenceAgreement
    {
        get
        {
            var total = PositiveEvidence + NegativeEvidence;
            return total <= 0 ? 0 : Math.Abs(PositiveEvidence - NegativeEvidence) / total;
        }
    }
    [NotMapped]
    public string RelevanceBand => RelevanceBands.Classify(AutomaticScore, AutomaticConfidence, ActiveFeedback).ToString().ToLowerInvariant();
}

public sealed class ArticleFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArticleId { get; set; }
    public Article? Article { get; set; }
    public FeedbackKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AvoidedTopicRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Phrase { get; set; }
    public required string NormalizedPhrase { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum FeedbackKind
{
    NeverThisTopic = -2,
    NotInterested = -1,
    Interested = 1,
    VeryInterested = 2
}

public enum RelevanceBand
{
    High,
    Maybe,
    Filtered
}

public static class RelevanceBands
{
    public const double HighScore = 0.65;
    public const double FilteredScore = 0.35;
    public const double RequiredConfidence = 0.50;

    public static RelevanceBand Classify(double automaticScore, double automaticConfidence, FeedbackKind? activeFeedback = null)
    {
        if (activeFeedback is FeedbackKind.Interested or FeedbackKind.VeryInterested) return RelevanceBand.High;
        if (activeFeedback is FeedbackKind.NotInterested or FeedbackKind.NeverThisTopic) return RelevanceBand.Filtered;
        if (automaticConfidence >= RequiredConfidence && automaticScore >= HighScore) return RelevanceBand.High;
        if (automaticConfidence >= RequiredConfidence && automaticScore <= FilteredScore) return RelevanceBand.Filtered;
        return RelevanceBand.Maybe;
    }
}

public sealed record ArticleCandidate(
    string ExternalId,
    string Title,
    string Link,
    string? Summary,
    string? Author,
    DateTimeOffset PublishedAt,
    Guid? FeedSourceId = null,
    string? FeedName = null);

public sealed record FeedbackExample(ArticleCandidate Article, FeedbackKind Kind);
public sealed record ScoreResult(
    double Value,
    string Reason,
    double? BaselineValue = null,
    string? BaselineReason = null,
    double Confidence = 0,
    int MatchingFeedbackCount = 0,
    string? ConfidenceReason = null,
    double PositiveEvidence = 0,
    double NegativeEvidence = 0);
