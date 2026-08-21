using Microsoft.EntityFrameworkCore;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class PersonalRssDbContext(DbContextOptions<PersonalRssDbContext> options) : DbContext(options)
{
    public DbSet<FeedSource> Feeds => Set<FeedSource>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ArticleFeedback> Feedback => Set<ArticleFeedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeedSource>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Slug).HasMaxLength(100);
            entity.Property(x => x.Url).HasMaxLength(2048);
        });
        modelBuilder.Entity<Article>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.FeedSourceId, x.PublishedAt });
            entity.HasIndex(x => new { x.FeedSourceId, x.ExternalId }).IsUnique();
            entity.Property(x => x.ExternalId).HasMaxLength(2048);
            entity.Property(x => x.Title).HasMaxLength(1000);
            entity.Property(x => x.Link).HasMaxLength(2048);
            entity.HasOne(x => x.FeedSource).WithMany().HasForeignKey(x => x.FeedSourceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ArticleFeedback>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.Article).WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
