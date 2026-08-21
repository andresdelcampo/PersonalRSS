using System.Security.Cryptography;
using System.Text;
using PersonalRSS.Core;

namespace PersonalRSS.Application;

public sealed class FeedRefreshService(IFeedRepository repository, IFeedFetcher fetcher, IScoringProvider scoringProvider)
{
    public async Task<RefreshResult> RefreshAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        var feed = await repository.GetFeedAsync(feedId, cancellationToken) ?? throw new KeyNotFoundException($"Feed {feedId} was not found.");
        try
        {
            var candidates = await fetcher.FetchAsync(feed, cancellationToken);
            var articles = new List<Article>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var score = await scoringProvider.ScoreAsync(candidate, cancellationToken);
                articles.Add(new Article { Id = StableId(feed.Id, candidate.ExternalId), FeedSourceId = feed.Id, ExternalId = candidate.ExternalId, Title = candidate.Title, Link = candidate.Link, Summary = candidate.Summary, Author = candidate.Author, PublishedAt = candidate.PublishedAt, Score = Math.Clamp(score.Value, 0, 1), ScoreReason = score.Reason });
            }
            await repository.UpsertArticlesAsync(articles, cancellationToken);
            feed.LastRefreshedAt = DateTimeOffset.UtcNow;
            feed.LastError = null;
            await repository.SaveFeedAsync(feed, cancellationToken);
            return new RefreshResult(feed.Id, candidates.Count, articles.Count, scoringProvider.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            feed.LastError = exception.Message;
            await repository.SaveFeedAsync(feed, cancellationToken);
            throw;
        }
    }

    private static Guid StableId(Guid feedId, string externalId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{feedId:N}:{externalId}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
