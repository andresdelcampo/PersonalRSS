using System.Net;
using System.Text.RegularExpressions;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public static partial class AvoidedTopicText
{
    public static string PrepareDisplayPhrase(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var display = Whitespace().Replace(WebUtility.HtmlDecode(phrase), " ").Trim();
        if (display.Length is < 2 or > 120)
            throw new ArgumentException("Enter a topic or phrase from 2 to 120 characters.", nameof(phrase));
        if (Normalize(display).Length < 2)
            throw new ArgumentException("Enter a topic or phrase containing letters or numbers.", nameof(phrase));
        return display;
    }

    public static string Normalize(string value) => Whitespace().Replace(NonTopicCharacters().Replace(
        WebUtility.HtmlDecode(value).ToLowerInvariant(), " "), " ").Trim();

    public static bool Matches(ArticleCandidate article, AvoidedTopicRule rule)
    {
        var summary = HtmlTags().Replace(article.Summary ?? string.Empty, " ");
        var searchable = Normalize($"{article.Title} {summary} {article.Author}");
        return $" {searchable} ".Contains($" {rule.NormalizedPhrase} ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"[^\p{L}\p{N}+#]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonTopicCharacters();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();
}
