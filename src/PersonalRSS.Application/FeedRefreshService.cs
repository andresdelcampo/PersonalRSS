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
            var scoringCandidates = candidates.Select(candidate => candidate with { FeedSourceId = feed.Id, FeedName = feed.Name }).ToList();
            IReadOnlyList<ScoreResult> scores;
            if (scoringProvider is IBatchScoringProvider batchScoringProvider)
                scores = await batchScoringProvider.ScoreAsync(scoringCandidates, cancellationToken);
            else
                scores = await Task.WhenAll(scoringCandidates.Select(candidate => scoringProvider.ScoreAsync(candidate, cancellationToken)));
            var articles = new List<Article>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var score = scores[index];
                var baseline = Math.Clamp(score.BaselineValue ?? score.Value, 0, 1);
                var automatic = Math.Clamp(score.Value, 0, 1);
                articles.Add(new Article
                {
                    Id = StableId(feed.Id, candidate.ExternalId),
                    FeedSourceId = feed.Id,
                    ExternalId = candidate.ExternalId,
                    Title = candidate.Title,
                    Link = candidate.Link,
                    Summary = candidate.Summary,
                    Author = candidate.Author,
                    PublishedAt = candidate.PublishedAt,
                    BaselineScore = baseline,
                    BaselineScoreReason = score.BaselineReason ?? score.Reason,
                    AutomaticScore = automatic,
                    AutomaticScoreReason = score.Reason,
                    Score = automatic,
                    ScoreReason = score.Reason
                });
            }
            var newPosts = await repository.UpsertArticlesAsync(articles, cancellationToken);
            await repository.UpdateFeedRefreshStateAsync(feed.Id, DateTimeOffset.UtcNow, null, cancellationToken);
            return new RefreshResult(feed.Id, candidates.Count, articles.Count, newPosts, scoringProvider.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await repository.UpdateFeedRefreshStateAsync(feed.Id, feed.LastRefreshedAt, exception.Message, cancellationToken);
            throw;
        }
    }

    private static Guid StableId(Guid feedId, string externalId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{feedId:N}:{externalId}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
