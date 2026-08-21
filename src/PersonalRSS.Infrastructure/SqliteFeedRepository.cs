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

    public async Task SaveFeedAsync(FeedSource feed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Feeds.Update(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var article in articles)
        {
            var existing = await db.Articles.SingleOrDefaultAsync(x => x.Id == article.Id, cancellationToken);
            if (existing is null) db.Articles.Add(article);
            else
            {
                existing.Title = article.Title; existing.Link = article.Link; existing.Summary = article.Summary;
                existing.Author = article.Author; existing.PublishedAt = article.PublishedAt;
                existing.Score = article.Score; existing.ScoreReason = article.ScoreReason;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
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
        return await query.OrderByDescending(x => x.PublishedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
    }

    public async Task AddFeedbackAsync(ArticleFeedback feedback, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Feedback.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);
    }
}
