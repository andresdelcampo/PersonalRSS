using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalRSS.Application;

namespace PersonalRSS.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersonalRssInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("PersonalRss") ?? "Data Source=data/personalrss.db";
        services.Configure<ScoringOptions>(configuration.GetSection(ScoringOptions.SectionName));
        services.AddPooledDbContextFactory<PersonalRssDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IFeedRepository, SqliteFeedRepository>();
        services.AddSingleton<KeywordScoringProvider>();
        services.AddSingleton<IScoringProvider, LocalPreferenceScoringProvider>();
        services.AddSingleton<IFilteredFeedRenderer, RssFeedRenderer>();
        services.AddSingleton<ISubscriptionListParser, OpmlSubscriptionListParser>();
        services.AddHttpClient<IFeedFetcher, HttpFeedFetcher>(client => { client.Timeout = TimeSpan.FromSeconds(30); client.DefaultRequestHeaders.UserAgent.ParseAdd("PersonalRSS/0.1"); });
        services.AddScoped<FeedRefreshService>();
        services.AddScoped<FeedImportService>();
        services.AddScoped<PreferenceRescoringService>();
        return services;
    }
}
