using PersonalRSS.Core;

namespace PersonalRSS.Application;

public interface IFeedRepository
{
    Task<IReadOnlyList<FeedSource>> GetFeedsAsync(CancellationToken cancellationToken = default);
    Task<FeedSource?> GetFeedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FeedSource?> GetFeedBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task AddFeedAsync(FeedSource feed, CancellationToken cancellationToken = default);
    Task SaveFeedAsync(FeedSource feed, CancellationToken cancellationToken = default);
    Task UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default);
    Task<Article?> GetArticleAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Article>> GetArticlesAsync(Guid? feedId, double minimumScore, int limit, CancellationToken cancellationToken = default);
    Task AddFeedbackAsync(ArticleFeedback feedback, CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IFeedFetcher { Task<IReadOnlyList<ArticleCandidate>> FetchAsync(FeedSource source, CancellationToken cancellationToken = default); }
public interface IScoringProvider { string Name { get; } Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default); }
public interface IFilteredFeedRenderer { string Render(FeedSource source, IReadOnlyList<Article> articles, Uri publicFeedUri); }
public sealed record RefreshResult(Guid FeedId, int Fetched, int Stored, string ScoringProvider);
