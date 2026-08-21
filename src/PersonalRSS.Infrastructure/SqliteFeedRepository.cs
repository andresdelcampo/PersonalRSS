using Microsoft.EntityFrameworkCore;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class SqliteFeedRepository(IDbContextFactory<PersonalRssDbContext> contextFactory) : IFeedRepository
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var check = db.Database.GetDbConnection().CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Feeds') WHERE name = 'LastViewedAt';";
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Feeds\" ADD COLUMN \"LastViewedAt\" TEXT NULL;", cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<IReadOnlyList<FeedSource>> GetFeedsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feeds.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<FeedSource?> GetFeedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feeds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<FeedSource?> GetFeedBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feeds.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == slug, cancellationToken);
    }

    public async Task AddFeedAsync(FeedSource feed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddFeedsAsync(IEnumerable<FeedSource> feeds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Feeds.AddRange(feeds);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveFeedAsync(FeedSource feed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Feeds.Update(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateFeedRefreshStateAsync(Guid id, DateTimeOffset? refreshedAt, string? error, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var feed = await db.Feeds.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (feed is null) return;
        feed.LastRefreshedAt = refreshedAt;
        feed.LastError = error;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> MarkFeedViewedAsync(Guid id, DateTimeOffset viewedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var feed = await db.Feeds.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (feed is null) return false;
        feed.LastViewedAt = viewedAt;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var articles = await db.Articles.AsNoTracking().Select(x => new { x.FeedSourceId, x.IngestedAt }).ToListAsync(cancellationToken);
        return articles
            .Where(article => viewedAt.TryGetValue(article.FeedSourceId, out var viewed) && (viewed is null || article.IngestedAt > viewed))
            .GroupBy(article => article.FeedSourceId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<int> UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var added = 0;
        foreach (var article in articles)
        {
            var existing = await db.Articles.SingleOrDefaultAsync(x => x.Id == article.Id, cancellationToken);
            if (existing is null)
            {
                db.Articles.Add(article);
                added++;
            }
            else
            {
                existing.Title = article.Title; existing.Link = article.Link; existing.Summary = article.Summary;
                existing.Author = article.Author; existing.PublishedAt = article.PublishedAt;
                existing.Score = article.Score; existing.ScoreReason = article.ScoreReason;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<Article?> GetArticleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Articles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Article>> GetArticlesAsync(Guid? feedId, double minimumScore, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Articles.AsNoTracking().Where(x => x.Score >= minimumScore);
        if (feedId.HasValue) query = query.Where(x => x.FeedSourceId == feedId.Value);
        var articles = await query.ToListAsync(cancellationToken);
        return articles.OrderByDescending(x => x.PublishedAt).Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    public async Task AddFeedbackAsync(ArticleFeedback feedback, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.SingleOrDefaultAsync(x => x.Id == feedback.ArticleId, cancellationToken)
            ?? throw new KeyNotFoundException($"Article {feedback.ArticleId} was not found.");
        article.Score = feedback.Kind == FeedbackKind.Interested ? 1 : 0;
        article.ScoreReason = feedback.Kind == FeedbackKind.Interested
            ? "You marked this article as interesting."
            : "You marked this article as not interesting.";
        db.Feedback.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);
    }
}
