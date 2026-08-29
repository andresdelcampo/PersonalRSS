using System.Net;
using System.Text.RegularExpressions;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

internal sealed record StoryFeedbackCluster(
    FeedbackExample Representative,
    IReadOnlyList<FeedbackExample> Members,
    string StableKey);

internal static partial class DuplicateStoryClusterer
{
    public static IReadOnlyList<StoryFeedbackCluster> Collapse(IReadOnlyList<FeedbackExample> examples)
    {
        if (examples.Count == 0) return [];

        var descriptions = examples.Select(Describe).ToArray();
        var parents = Enumerable.Range(0, examples.Count).ToArray();
        for (var left = 0; left < examples.Count; left++)
        {
            for (var right = left + 1; right < examples.Count; right++)
            {
                if (SameStory(descriptions[left], descriptions[right])) Union(parents, left, right);
            }
        }

        return Enumerable.Range(0, examples.Count)
            .GroupBy(index => Find(parents, index))
            .Select(group => BuildCluster(group.Select(index => examples[index]).ToArray()))
            .Where(cluster => cluster is not null)
            .Select(cluster => cluster!)
            .OrderBy(cluster => cluster.StableKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static string CandidateKey(ArticleCandidate article) =>
        $"{article.FeedSourceId?.ToString("N") ?? "none"}:{article.ExternalId}";

    private static StoryFeedbackCluster? BuildCluster(IReadOnlyList<FeedbackExample> members)
    {
        var signedTotal = members.Sum(member => (int)member.Kind);
        if (signedTotal == 0) return null;

        var direction = Math.Sign(signedTotal);
        var sameDirection = members.All(member => Math.Sign((int)member.Kind) == direction);
        var averageMagnitude = members.Average(member => Math.Abs((int)member.Kind));
        var kind = direction > 0
            ? sameDirection && averageMagnitude >= 1.5 ? FeedbackKind.VeryInterested : FeedbackKind.Interested
            : sameDirection && averageMagnitude >= 1.5 ? FeedbackKind.NeverThisTopic : FeedbackKind.NotInterested;
        var source = members
            .OrderByDescending(member => (member.Article.Title?.Length ?? 0) + (member.Article.Summary?.Length ?? 0))
            .ThenBy(member => CandidateKey(member.Article), StringComparer.Ordinal)
            .First();
        var representative = new FeedbackExample(source.Article, kind);
        var stableKey = members.Select(member => CandidateKey(member.Article)).OrderBy(value => value, StringComparer.Ordinal).First();
        return new StoryFeedbackCluster(representative, members, stableKey);
    }

    private static StoryDescription Describe(FeedbackExample example)
    {
        var title = NormalizeTitle(example.Article.Title);
        var tokens = TitleTokens().Matches(title).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new StoryDescription(CanonicalUrl(example.Article.Link), title, tokens, SimHash(tokens));
    }

    private static bool SameStory(StoryDescription left, StoryDescription right)
    {
        if (left.CanonicalUrl is not null && string.Equals(left.CanonicalUrl, right.CanonicalUrl, StringComparison.OrdinalIgnoreCase)) return true;
        if (left.NormalizedTitle.Length >= 12 && string.Equals(left.NormalizedTitle, right.NormalizedTitle, StringComparison.Ordinal)) return true;
        if (left.Tokens.Count < 5 || right.Tokens.Count < 5 || HammingDistance(left.SimHash, right.SimHash) > 4) return false;
        var intersection = left.Tokens.Intersect(right.Tokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = left.Tokens.Union(right.Tokens, StringComparer.OrdinalIgnoreCase).Count();
        return union > 0 && intersection / (double)union >= 0.85;
    }

    private static string? CanonicalUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        if (string.IsNullOrEmpty(path)) path = "/";
        var query = QueryPart().Matches(uri.Query)
            .Select(match => (Name: match.Groups["name"].Value, Value: match.Groups["value"].Value))
            .Where(item => !IsTrackingParameter(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => $"{item.Name.ToLowerInvariant()}={item.Value}")
            .ToArray();
        return $"{host}{path}{(query.Length == 0 ? string.Empty : "?" + string.Join("&", query))}";
    }

    private static bool IsTrackingParameter(string name) =>
        name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("fbclid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("gclid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("mc_cid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("mc_eid", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ref", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("source", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitle(string value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty).ToLowerInvariant();
        return Whitespace().Replace(NonTitleCharacters().Replace(decoded, " "), " ").Trim();
    }

    private static ulong SimHash(IEnumerable<string> tokens)
    {
        Span<int> totals = stackalloc int[64];
        foreach (var token in tokens)
        {
            var hash = StableHash(token);
            for (var bit = 0; bit < 64; bit++) totals[bit] += (hash & (1UL << bit)) == 0 ? -1 : 1;
        }
        ulong result = 0;
        for (var bit = 0; bit < 64; bit++) if (totals[bit] >= 0) result |= 1UL << bit;
        return result;
    }

    internal static ulong StableHash(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    private static int HammingDistance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);

    private static int Find(int[] parents, int value)
    {
        while (parents[value] != value)
        {
            parents[value] = parents[parents[value]];
            value = parents[value];
        }
        return value;
    }

    private static void Union(int[] parents, int left, int right)
    {
        left = Find(parents, left);
        right = Find(parents, right);
        if (left != right) parents[right] = left;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}+#.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonTitleCharacters();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}+#.-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTokens();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?:^|[?&])(?<name>[^=&]+)(?:=(?<value>[^&]*))?", RegexOptions.CultureInvariant)]
    private static partial Regex QueryPart();

    private sealed record StoryDescription(string? CanonicalUrl, string NormalizedTitle, HashSet<string> Tokens, ulong SimHash);
}
