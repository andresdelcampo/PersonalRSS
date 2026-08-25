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
            await EnsureColumnAsync(db, "Feeds", "LastViewedAt", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "ReadAt", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "IsUnreadPinned", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            var addedBaselineScore = await EnsureColumnAsync(db, "Articles", "BaselineScore", "REAL NOT NULL DEFAULT 0.5", cancellationToken);
            var addedBaselineReason = await EnsureColumnAsync(db, "Articles", "BaselineScoreReason", "TEXT NULL", cancellationToken);
            var addedAutomaticScore = await EnsureColumnAsync(db, "Articles", "AutomaticScore", "REAL NOT NULL DEFAULT 0.5", cancellationToken);
            var addedAutomaticReason = await EnsureColumnAsync(db, "Articles", "AutomaticScoreReason", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "AutomaticConfidence", "REAL NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "MatchingFeedbackCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "ConfidenceReason", "TEXT NULL", cancellationToken);
            if (addedBaselineScore) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"BaselineScore\" = \"Score\";", cancellationToken);
            if (addedBaselineReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"BaselineScoreReason\" = \"ScoreReason\";", cancellationToken);
            if (addedAutomaticScore) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"AutomaticScore\" = \"Score\";", cancellationToken);
            if (addedAutomaticReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"AutomaticScoreReason\" = \"ScoreReason\";", cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> EnsureColumnAsync(PersonalRssDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';";
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) == 0) return false;
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0) return false;
        check.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
        await check.ExecuteNonQueryAsync(cancellationToken);
        return true;
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

    public async Task<bool> DeleteFeedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var feed = await db.Feeds.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (feed is null) return false;
        db.Feeds.Remove(feed);
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
        var pinnedArticles = await db.Articles.Where(x => x.FeedSourceId == id && x.IsUnreadPinned).ToListAsync(cancellationToken);
        foreach (var article in pinnedArticles) article.IsUnreadPinned = false;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkFeedUnreadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var feed = await db.Feeds.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (feed is null) return false;
        feed.LastViewedAt = null;
        var articles = await db.Articles.Where(x => x.FeedSourceId == id).ToListAsync(cancellationToken);
        foreach (var article in articles)
        {
            article.ReadAt = null;
            article.IsUnreadPinned = false;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsAsync(double minimumScore = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var articles = await db.Articles.AsNoTracking().Where(x => x.Score >= minimumScore).Select(x => new { x.FeedSourceId, x.IngestedAt, x.ReadAt, x.IsUnreadPinned }).ToListAsync(cancellationToken);
        return articles
            .Where(article => viewedAt.TryGetValue(article.FeedSourceId, out var viewed) &&
                (article.IsUnreadPinned || (article.ReadAt is null && (viewed is null || article.IngestedAt > viewed))))
            .GroupBy(article => article.FeedSourceId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsByBandAsync(RelevanceBand band, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var articles = await db.Articles.AsNoTracking().ToListAsync(cancellationToken);
        var articleIds = articles.Select(x => x.Id).ToHashSet();
        var activeFeedback = (await db.Feedback.AsNoTracking().Where(x => articleIds.Contains(x.ArticleId)).ToListAsync(cancellationToken))
            .GroupBy(x => x.ArticleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First().Kind);
        return articles
            .Where(article => RelevanceBands.Classify(article.AutomaticScore, article.AutomaticConfidence,
                    activeFeedback.GetValueOrDefault(article.Id)) == band &&
                viewedAt.TryGetValue(article.FeedSourceId, out var viewed) &&
                (article.IsUnreadPinned || (article.ReadAt is null && (viewed is null || article.IngestedAt > viewed))))
            .GroupBy(article => article.FeedSourceId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<int> UpsertArticlesAsync(IEnumerable<Article> articles, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ratedArticleIds = (await db.Feedback.AsNoTracking().Select(x => x.ArticleId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
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
                existing.BaselineScore = article.BaselineScore; existing.BaselineScoreReason = article.BaselineScoreReason;
                existing.AutomaticScore = article.AutomaticScore; existing.AutomaticScoreReason = article.AutomaticScoreReason;
                existing.AutomaticConfidence = article.AutomaticConfidence;
                existing.MatchingFeedbackCount = article.MatchingFeedbackCount;
                existing.ConfidenceReason = article.ConfidenceReason;
                if (!ratedArticleIds.Contains(existing.Id))
                {
                    existing.Score = article.Score; existing.ScoreReason = article.ScoreReason;
                }
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
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var articleIds = articles.Select(x => x.Id).ToHashSet();
        var activeFeedback = (await db.Feedback.AsNoTracking()
                .Where(x => articleIds.Contains(x.ArticleId))
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.ArticleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First().Kind);
        foreach (var article in articles)
        {
            if (activeFeedback.TryGetValue(article.Id, out var kind)) article.ActiveFeedback = kind;
            article.IsUnread = viewedAt.TryGetValue(article.FeedSourceId, out var viewed) &&
                (article.IsUnreadPinned || (article.ReadAt is null && (viewed is null || article.IngestedAt > viewed)));
        }
        return articles.OrderByDescending(x => x.PublishedAt).Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    public async Task<bool> SetArticleReadStateAsync(Guid articleId, bool isUnread, bool automatic, DateTimeOffset changedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.SingleOrDefaultAsync(x => x.Id == articleId, cancellationToken);
        if (article is null) return false;
        if (automatic && article.IsUnreadPinned) return true;
        article.ReadAt = isUnread ? null : changedAt;
        article.IsUnreadPinned = isUnread || (automatic && article.IsUnreadPinned);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> MarkArticlesReadAsync(IReadOnlyCollection<Guid> articleIds, bool automatic, DateTimeOffset readAt, CancellationToken cancellationToken = default)
        => await SetArticlesReadStateAsync(articleIds, false, automatic, readAt, cancellationToken);

    public async Task<int> SetArticlesReadStateAsync(IReadOnlyCollection<Guid> articleIds, bool isUnread, bool automatic, DateTimeOffset changedAt, CancellationToken cancellationToken = default)
    {
        if (articleIds.Count == 0) return 0;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ids = articleIds.Distinct().ToHashSet();
        var articles = await db.Articles.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var article in articles)
        {
            if (automatic && isUnread) continue;
            if (automatic && article.IsUnreadPinned) continue;
            article.ReadAt = isUnread ? null : changedAt;
            article.IsUnreadPinned = isUnread || (automatic && article.IsUnreadPinned);
            changed++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    public async Task<IReadOnlyList<FeedbackExample>> GetFeedbackExamplesAsync(Guid? excludingArticleId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = from feedback in db.Feedback.AsNoTracking()
                    join article in db.Articles.AsNoTracking() on feedback.ArticleId equals article.Id
                    join feed in db.Feeds.AsNoTracking() on article.FeedSourceId equals feed.Id
                    select new { feedback, article, feed.Name };
        if (excludingArticleId.HasValue) query = query.Where(item => item.article.Id != excludingArticleId.Value);
        var rows = await query.ToListAsync(cancellationToken);
        return rows.Select(item => new FeedbackExample(
            new ArticleCandidate(item.article.ExternalId, item.article.Title, item.article.Link, item.article.Summary,
                item.article.Author, item.article.PublishedAt, item.article.FeedSourceId, item.Name),
            item.feedback.Kind)).ToList();
    }

    public async Task SetFeedbackAsync(Guid articleId, FeedbackKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.SingleOrDefaultAsync(x => x.Id == articleId, cancellationToken)
            ?? throw new KeyNotFoundException($"Article {articleId} was not found.");
        var previous = await db.Feedback.Where(x => x.ArticleId == articleId).ToListAsync(cancellationToken);
        db.Feedback.RemoveRange(previous);
        article.Score = kind switch
        {
            FeedbackKind.VeryInterested => 1,
            FeedbackKind.Interested => 0.9,
            FeedbackKind.NotInterested => 0.1,
            FeedbackKind.NeverThisTopic => 0,
            _ => article.AutomaticScore
        };
        article.ScoreReason = kind switch
        {
            FeedbackKind.VeryInterested => "You marked this article as very interesting; it supplies strong positive learning evidence.",
            FeedbackKind.Interested => "You marked this article as interesting; it supplies positive learning evidence.",
            FeedbackKind.NotInterested => "You marked this article as not interesting; it supplies negative learning evidence.",
            FeedbackKind.NeverThisTopic => "You marked this topic as unwanted; it supplies strong negative learning evidence.",
            _ => article.AutomaticScoreReason
        };
        db.Feedback.Add(new ArticleFeedback { ArticleId = articleId, Kind = kind });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearFeedbackAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.SingleOrDefaultAsync(x => x.Id == articleId, cancellationToken)
            ?? throw new KeyNotFoundException($"Article {articleId} was not found.");
        var previous = await db.Feedback.Where(x => x.ArticleId == articleId).ToListAsync(cancellationToken);
        db.Feedback.RemoveRange(previous);
        article.Score = article.AutomaticScore;
        article.ScoreReason = article.AutomaticScoreReason;
        await db.SaveChangesAsync(cancellationToken);
    }
}
