using PersonalRSS.Core;

namespace PersonalRSS.Application;

public interface IFeedRepository
{
    Task<IReadOnlyList<FeedSource>> GetFeedsAsync(CancellationToken cancellationToken = default);
    Task<FeedSource?> GetFeedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FeedSource?> GetFeedBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task AddFeedAsync(FeedSource feed, CancellationToken cancellationToken = default);
    Task AddFeedsAsync(IEnumerable<FeedSource> feeds, CancellationToken cancellationToken = default);
    Task SaveFeedAsync(FeedSource feed, CancellationToken cancellationToken = default);
    Task UpdateFeedRefreshStateAsync(Guid id, DateTimeOffset? refreshedAt, string? error, CancellationToken cancellationToken = default);
    Task<bool> MarkFeedViewedAsync(Guid id, DateTimeOffset viewedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsAsync(CancellationToken cancellationToken = default);
    Task<int> UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default);
    Task<Article?> GetArticleAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Article>> GetArticlesAsync(Guid? feedId, double minimumScore, int limit, CancellationToken cancellationToken = default);
    Task AddFeedbackAsync(ArticleFeedback feedback, CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IFeedFetcher { Task<IReadOnlyList<ArticleCandidate>> FetchAsync(FeedSource source, CancellationToken cancellationToken = default); }
public interface IScoringProvider { string Name { get; } Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default); }
public interface IFilteredFeedRenderer { string Render(FeedSource source, IReadOnlyList<Article> articles, Uri publicFeedUri); }
public interface ISubscriptionListParser { Task<IReadOnlyList<SubscriptionCandidate>> ParseAsync(Stream content, CancellationToken cancellationToken = default); }
public sealed record SubscriptionCandidate(string Name, string Url, string? Folder);
public sealed record FeedImportIssue(string? Name, string? Url, string Reason);
public sealed record FeedImportResult(int Added, int Skipped, int Invalid, IReadOnlyList<FeedImportIssue> Issues);
public sealed record RefreshResult(Guid FeedId, int Fetched, int Stored, int NewPosts, string ScoringProvider);
