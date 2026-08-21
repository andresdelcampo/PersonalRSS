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
    public double Score { get; set; }
    public string? ScoreReason { get; set; }
}

public sealed class ArticleFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArticleId { get; set; }
    public Article? Article { get; set; }
    public FeedbackKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum FeedbackKind { NotInterested = -1, Interested = 1 }
public sealed record ArticleCandidate(string ExternalId, string Title, string Link, string? Summary, string? Author, DateTimeOffset PublishedAt);
public sealed record ScoreResult(double Value, string Reason);
