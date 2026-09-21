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
            await EnsureColumnAsync(db, "Articles", "PositiveEvidence", "REAL NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "NegativeEvidence", "REAL NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "ConfidenceReason", "TEXT NULL", cancellationToken);
            var addedLocalScore = await EnsureColumnAsync(db, "Articles", "LocalAutomaticScore", "REAL NOT NULL DEFAULT 0.5", cancellationToken);
            var addedLocalReason = await EnsureColumnAsync(db, "Articles", "LocalAutomaticScoreReason", "TEXT NULL", cancellationToken);
            var addedLocalConfidence = await EnsureColumnAsync(db, "Articles", "LocalAutomaticConfidence", "REAL NOT NULL DEFAULT 0", cancellationToken);
            var addedLocalConfidenceReason = await EnsureColumnAsync(db, "Articles", "LocalConfidenceReason", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiDirection", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiScore", "REAL NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiConfidence", "REAL NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiReason", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiMatchedExample", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiModel", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(db, "Articles", "LocalAiAssessedAt", "TEXT NULL", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "AvoidedTopicRules" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AvoidedTopicRules" PRIMARY KEY,
                    "Phrase" TEXT NOT NULL,
                    "NormalizedPhrase" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AvoidedTopicRules_NormalizedPhrase\" ON \"AvoidedTopicRules\" (\"NormalizedPhrase\");", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "PreferredTopicRules" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_PreferredTopicRules" PRIMARY KEY,
                    "Phrase" TEXT NOT NULL,
                    "NormalizedPhrase" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                """, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PreferredTopicRules_NormalizedPhrase\" ON \"PreferredTopicRules\" (\"NormalizedPhrase\");", cancellationToken);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "LocalAiSettings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_LocalAiSettings" PRIMARY KEY,
                    "Enabled" INTEGER NOT NULL,
                    "Endpoint" TEXT NOT NULL,
                    "Model" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """, cancellationToken);
            if (addedBaselineScore) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"BaselineScore\" = \"Score\";", cancellationToken);
            if (addedBaselineReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"BaselineScoreReason\" = \"ScoreReason\";", cancellationToken);
            if (addedAutomaticScore) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"AutomaticScore\" = \"Score\";", cancellationToken);
            if (addedAutomaticReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"AutomaticScoreReason\" = \"ScoreReason\";", cancellationToken);
            if (addedLocalScore) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"LocalAutomaticScore\" = \"AutomaticScore\";", cancellationToken);
            if (addedLocalReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"LocalAutomaticScoreReason\" = \"AutomaticScoreReason\";", cancellationToken);
            if (addedLocalConfidence) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"LocalAutomaticConfidence\" = \"AutomaticConfidence\";", cancellationToken);
            if (addedLocalConfidenceReason) await db.Database.ExecuteSqlRawAsync("UPDATE \"Articles\" SET \"LocalConfidenceReason\" = \"ConfidenceReason\";", cancellationToken);
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
        var localAiEnabled = (await db.LocalAiSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken))?.Enabled == true;
        var added = 0;
        foreach (var article in articles)
        {
            if (article.LocalAutomaticScoreReason is null && article.AutomaticScoreReason is not null)
            {
                article.LocalAutomaticScore = article.AutomaticScore;
                article.LocalAutomaticScoreReason = article.AutomaticScoreReason;
                article.LocalAutomaticConfidence = article.AutomaticConfidence;
                article.LocalConfidenceReason = article.ConfidenceReason;
            }
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
                existing.LocalAutomaticScore = article.LocalAutomaticScore;
                existing.LocalAutomaticScoreReason = article.LocalAutomaticScoreReason;
                existing.LocalAutomaticConfidence = article.LocalAutomaticConfidence;
                existing.LocalConfidenceReason = article.LocalConfidenceReason;
                var useAi = localAiEnabled && existing.LocalAiScore.HasValue && existing.LocalAiConfidence >= 0.8;
                existing.AutomaticScore = useAi ? existing.LocalAiScore!.Value : article.LocalAutomaticScore;
                existing.AutomaticScoreReason = useAi ? existing.LocalAiReason : article.LocalAutomaticScoreReason;
                existing.AutomaticConfidence = useAi ? existing.LocalAiConfidence!.Value : article.LocalAutomaticConfidence;
                existing.ConfidenceReason = useAi
                    ? $"Local AI semantic assessment ({existing.LocalAiConfidence:0.00}) matched a rated example."
                    : article.LocalConfidenceReason;
                existing.MatchingFeedbackCount = article.MatchingFeedbackCount;
                existing.PositiveEvidence = article.PositiveEvidence;
                existing.NegativeEvidence = article.NegativeEvidence;
                if (!ratedArticleIds.Contains(existing.Id))
                {
                    existing.Score = existing.AutomaticScore; existing.ScoreReason = existing.AutomaticScoreReason;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<Article?> GetArticleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (article is null) return null;
        var feedback = await db.Feedback.AsNoTracking()
            .Where(x => x.ArticleId == id)
            .ToListAsync(cancellationToken);
        article.ActiveFeedback = feedback.OrderByDescending(x => x.CreatedAt)
            .Select(x => (FeedbackKind?)x.Kind).FirstOrDefault();
        return article;
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

    public async Task<IReadOnlyList<Article>> GetUnreadArticlesByBandsAsync(IReadOnlyCollection<RelevanceBand> bands, CancellationToken cancellationToken = default)
    {
        if (bands.Count == 0) return [];
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var articles = await db.Articles.AsNoTracking().ToListAsync(cancellationToken);
        var articleIds = articles.Select(x => x.Id).ToHashSet();
        var activeFeedback = (await db.Feedback.AsNoTracking()
                .Where(x => articleIds.Contains(x.ArticleId))
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.ArticleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First().Kind);
        var includedBands = bands.ToHashSet();
        foreach (var article in articles)
        {
            if (activeFeedback.TryGetValue(article.Id, out var kind)) article.ActiveFeedback = kind;
            article.IsUnread = viewedAt.TryGetValue(article.FeedSourceId, out var viewed) &&
                (article.IsUnreadPinned || (article.ReadAt is null && (viewed is null || article.IngestedAt > viewed)));
        }
        return articles
            .Where(article => article.IsUnread && includedBands.Contains(RelevanceBands.Classify(
                article.AutomaticScore, article.AutomaticConfidence, article.ActiveFeedback)))
            .OrderByDescending(article => article.PublishedAt)
            .ToList();
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

    public async Task<IReadOnlyList<StoredArticleForScoring>> GetArticlesForRescoringAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (from article in db.Articles.AsNoTracking()
                          join feed in db.Feeds.AsNoTracking() on article.FeedSourceId equals feed.Id
                          select new { article, feed.Name }).ToListAsync(cancellationToken);
        return rows.Select(item => new StoredArticleForScoring(item.article.Id,
            new ArticleCandidate(item.article.ExternalId, item.article.Title, item.article.Link, item.article.Summary,
                item.article.Author, item.article.PublishedAt, item.article.FeedSourceId, item.Name))).ToList();
    }

    public async Task<int> UpdateAutomaticScoresAsync(IReadOnlyCollection<AutomaticScoreUpdate> updates, CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return 0;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var byId = updates.ToDictionary(update => update.ArticleId);
        var articles = await db.Articles.ToListAsync(cancellationToken);
        var ratedArticleIds = (await db.Feedback.AsNoTracking().Select(feedback => feedback.ArticleId)
            .Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var changed = 0;
        foreach (var article in articles)
        {
            if (!byId.TryGetValue(article.Id, out var update)) continue;
            article.BaselineScore = update.BaselineScore;
            article.BaselineScoreReason = update.BaselineReason;
            article.AutomaticScore = update.AutomaticScore;
            article.AutomaticScoreReason = update.AutomaticReason;
            article.AutomaticConfidence = update.Confidence;
            article.LocalAutomaticScore = update.AutomaticScore;
            article.LocalAutomaticScoreReason = update.AutomaticReason;
            article.LocalAutomaticConfidence = update.Confidence;
            article.LocalConfidenceReason = update.ConfidenceReason;
            article.LocalAiDirection = null;
            article.LocalAiScore = null;
            article.LocalAiConfidence = null;
            article.LocalAiReason = null;
            article.LocalAiMatchedExample = null;
            article.LocalAiModel = null;
            article.LocalAiAssessedAt = null;
            article.MatchingFeedbackCount = update.MatchingFeedbackCount;
            article.PositiveEvidence = update.PositiveEvidence;
            article.NegativeEvidence = update.NegativeEvidence;
            article.ConfidenceReason = update.ConfidenceReason;
            changed++;
            if (ratedArticleIds.Contains(article.Id)) continue;
            article.Score = update.AutomaticScore;
            article.ScoreReason = update.AutomaticReason;
        }
        await db.SaveChangesAsync(cancellationToken);
        return changed;
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

    public async Task<IReadOnlyList<AvoidedTopicRule>> GetAvoidedTopicRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AvoidedTopicRules.AsNoTracking().OrderBy(rule => rule.Phrase).ToListAsync(cancellationToken);
    }

    public async Task<AvoidedTopicRule> AddAvoidedTopicRuleAsync(string phrase, CancellationToken cancellationToken = default)
    {
        var displayPhrase = AvoidedTopicText.PrepareDisplayPhrase(phrase);
        var normalizedPhrase = AvoidedTopicText.Normalize(displayPhrase);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AvoidedTopicRules.AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.NormalizedPhrase == normalizedPhrase, cancellationToken);
        if (existing is not null) return existing;
        if (await db.PreferredTopicRules.AsNoTracking().AnyAsync(rule => rule.NormalizedPhrase == normalizedPhrase, cancellationToken))
            throw new InvalidOperationException("That topic is already in Always-included topics.");
        var rule = new AvoidedTopicRule { Phrase = displayPhrase, NormalizedPhrase = normalizedPhrase };
        db.AvoidedTopicRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<AvoidedTopicRule?> UpdateAvoidedTopicRuleAsync(Guid id, string phrase, CancellationToken cancellationToken = default)
    {
        var displayPhrase = AvoidedTopicText.PrepareDisplayPhrase(phrase);
        var normalizedPhrase = AvoidedTopicText.Normalize(displayPhrase);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.AvoidedTopicRules.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null) return null;
        var duplicate = await db.AvoidedTopicRules.AsNoTracking()
            .AnyAsync(item => item.Id != id && item.NormalizedPhrase == normalizedPhrase, cancellationToken);
        if (duplicate) throw new InvalidOperationException("That avoided topic already exists.");
        if (await db.PreferredTopicRules.AsNoTracking().AnyAsync(item => item.NormalizedPhrase == normalizedPhrase, cancellationToken))
            throw new InvalidOperationException("That topic is already in Always-included topics.");
        rule.Phrase = displayPhrase;
        rule.NormalizedPhrase = normalizedPhrase;
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<bool> DeleteAvoidedTopicRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.AvoidedTopicRules.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null) return false;
        db.AvoidedTopicRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PreferredTopicRule>> GetPreferredTopicRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PreferredTopicRules.AsNoTracking().OrderBy(rule => rule.Phrase).ToListAsync(cancellationToken);
    }

    public async Task<PreferredTopicRule> AddPreferredTopicRuleAsync(string phrase, CancellationToken cancellationToken = default)
    {
        var displayPhrase = AvoidedTopicText.PrepareDisplayPhrase(phrase);
        var normalizedPhrase = AvoidedTopicText.Normalize(displayPhrase);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PreferredTopicRules.AsNoTracking()
            .SingleOrDefaultAsync(rule => rule.NormalizedPhrase == normalizedPhrase, cancellationToken);
        if (existing is not null) return existing;
        if (await db.AvoidedTopicRules.AsNoTracking().AnyAsync(rule => rule.NormalizedPhrase == normalizedPhrase, cancellationToken))
            throw new InvalidOperationException("That topic is already in Never topics.");
        var rule = new PreferredTopicRule { Phrase = displayPhrase, NormalizedPhrase = normalizedPhrase };
        db.PreferredTopicRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<PreferredTopicRule?> UpdatePreferredTopicRuleAsync(Guid id, string phrase, CancellationToken cancellationToken = default)
    {
        var displayPhrase = AvoidedTopicText.PrepareDisplayPhrase(phrase);
        var normalizedPhrase = AvoidedTopicText.Normalize(displayPhrase);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.PreferredTopicRules.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null) return null;
        var duplicate = await db.PreferredTopicRules.AsNoTracking()
            .AnyAsync(item => item.Id != id && item.NormalizedPhrase == normalizedPhrase, cancellationToken);
        if (duplicate) throw new InvalidOperationException("That always-included topic already exists.");
        if (await db.AvoidedTopicRules.AsNoTracking().AnyAsync(item => item.NormalizedPhrase == normalizedPhrase, cancellationToken))
            throw new InvalidOperationException("That topic is already in Never topics.");
        rule.Phrase = displayPhrase;
        rule.NormalizedPhrase = normalizedPhrase;
        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<bool> DeletePreferredTopicRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.PreferredTopicRules.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rule is null) return false;
        db.PreferredTopicRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<LocalAiSettings> GetLocalAiSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.LocalAiSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken)
            ?? new LocalAiSettings();
    }

    public async Task<LocalAiSettings> SaveLocalAiSettingsAsync(bool enabled, string endpoint, string model, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.LocalAiSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        var connectionChanged = settings is not null &&
            (!string.Equals(settings.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(settings.Model, model, StringComparison.Ordinal));
        if (settings is null)
        {
            settings = new LocalAiSettings();
            db.LocalAiSettings.Add(settings);
        }
        settings.Enabled = enabled;
        settings.Endpoint = endpoint;
        settings.Model = model;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        var ratedIds = (await db.Feedback.AsNoTracking().Select(x => x.ArticleId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var assessed = await db.Articles.Where(x => x.LocalAiAssessedAt != null).ToListAsync(cancellationToken);
        foreach (var article in assessed)
        {
            if (connectionChanged)
            {
                article.LocalAiDirection = null;
                article.LocalAiScore = null;
                article.LocalAiConfidence = null;
                article.LocalAiReason = null;
                article.LocalAiMatchedExample = null;
                article.LocalAiModel = null;
                article.LocalAiAssessedAt = null;
            }
            var useAi = enabled && article.LocalAiScore.HasValue && article.LocalAiConfidence >= 0.8;
            article.AutomaticScore = useAi ? article.LocalAiScore!.Value : article.LocalAutomaticScore;
            article.AutomaticScoreReason = useAi ? article.LocalAiReason : article.LocalAutomaticScoreReason;
            article.AutomaticConfidence = useAi ? article.LocalAiConfidence!.Value : article.LocalAutomaticConfidence;
            article.ConfidenceReason = useAi
                ? $"Local AI semantic assessment ({article.LocalAiConfidence:0.00}) matched a rated example."
                : article.LocalConfidenceReason;
            if (!ratedIds.Contains(article.Id))
            {
                article.Score = article.AutomaticScore;
                article.ScoreReason = article.AutomaticScoreReason;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<IReadOnlyList<LocalAiReviewCandidate>> GetLocalAiReviewCandidatesAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ratedIds = (await db.Feedback.AsNoTracking().Select(x => x.ArticleId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var viewedAt = await db.Feeds.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.LastViewedAt, cancellationToken);
        var rows = await (from article in db.Articles.AsNoTracking()
                          join feed in db.Feeds.AsNoTracking() on article.FeedSourceId equals feed.Id
                          where article.LocalAiAssessedAt == null
                          select new { article, feed.Name }).ToListAsync(cancellationToken);
        return rows
            .Where(item => !ratedIds.Contains(item.article.Id) &&
                viewedAt.TryGetValue(item.article.FeedSourceId, out var viewed) &&
                (item.article.IsUnreadPinned || (item.article.ReadAt is null && (viewed is null || item.article.IngestedAt > viewed))) &&
                item.article.LocalAutomaticConfidence < 1)
            .OrderByDescending(item => item.article.PublishedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(item => new LocalAiReviewCandidate(item.article.Id,
                new ArticleCandidate(item.article.ExternalId, item.article.Title, item.article.Link, item.article.Summary,
                    item.article.Author, item.article.PublishedAt, item.article.FeedSourceId, item.Name)))
            .ToList();
    }

    public async Task ApplyLocalAiAssessmentAsync(LocalAiAssessment assessment, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var article = await db.Articles.SingleOrDefaultAsync(x => x.Id == assessment.ArticleId, cancellationToken);
        if (article is null) return;
        article.LocalAiDirection = assessment.Direction;
        article.LocalAiScore = assessment.SuggestedScore;
        article.LocalAiConfidence = assessment.Confidence;
        article.LocalAiReason = assessment.Reason;
        article.LocalAiMatchedExample = assessment.MatchedExample;
        article.LocalAiModel = assessment.Model;
        article.LocalAiAssessedAt = assessment.AssessedAt;

        var settings = await db.LocalAiSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        var hasManualFeedback = await db.Feedback.AsNoTracking().AnyAsync(x => x.ArticleId == article.Id, cancellationToken);
        var useAi = settings?.Enabled == true && assessment.SuggestedScore.HasValue && assessment.Confidence >= 0.8;
        article.AutomaticScore = useAi ? assessment.SuggestedScore!.Value : article.LocalAutomaticScore;
        article.AutomaticScoreReason = useAi ? assessment.Reason : article.LocalAutomaticScoreReason;
        article.AutomaticConfidence = useAi ? assessment.Confidence : article.LocalAutomaticConfidence;
        article.ConfidenceReason = useAi
            ? $"Local AI semantic assessment ({assessment.Confidence:0.00}) matched a rated example."
            : article.LocalConfidenceReason;
        if (!hasManualFeedback)
        {
            article.Score = article.AutomaticScore;
            article.ScoreReason = article.AutomaticScoreReason;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
