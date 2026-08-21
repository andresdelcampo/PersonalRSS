using System.Text.RegularExpressions;
using PersonalRSS.Core;

namespace PersonalRSS.Application;

public sealed class FeedImportService(IFeedRepository repository, ISubscriptionListParser parser)
{
    public async Task<FeedImportResult> ImportAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var subscriptions = await parser.ParseAsync(content, cancellationToken);
        var existingFeeds = await repository.GetFeedsAsync(cancellationToken);
        var knownUrls = existingFeeds.Select(feed => NormalizeUrl(feed.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownSlugs = existingFeeds.Select(feed => feed.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<FeedSource>();
        var issues = new List<FeedImportIssue>();
        var skipped = 0;

        foreach (var subscription in subscriptions)
        {
            if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                issues.Add(new FeedImportIssue(subscription.Name, subscription.Url, "Feed URL is not an absolute HTTP(S) URL."));
                continue;
            }

            var normalizedUrl = NormalizeUrl(uri.ToString());
            if (!knownUrls.Add(normalizedUrl))
            {
                skipped++;
                continue;
            }

            var name = string.IsNullOrWhiteSpace(subscription.Name) ? uri.Host : subscription.Name.Trim();
            var slug = UniqueSlug(name, knownSlugs);
            additions.Add(new FeedSource { Name = name, Slug = slug, Url = uri.ToString() });
        }

        if (additions.Count > 0) await repository.AddFeedsAsync(additions, cancellationToken);
        return new FeedImportResult(additions.Count, skipped, issues.Count, issues);
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return value.Trim();
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if ((builder.Scheme == "http" && builder.Port == 80) || (builder.Scheme == "https" && builder.Port == 443)) builder.Port = -1;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string UniqueSlug(string name, ISet<string> knownSlugs)
    {
        var basis = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(basis)) basis = "feed";
        var candidate = basis;
        for (var suffix = 2; !knownSlugs.Add(candidate); suffix++) candidate = $"{basis}-{suffix}";
        return candidate;
    }
}
