using Microsoft.Extensions.Options;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

namespace PersonalRSS.Tests;

public sealed class KeywordScoringProviderTests
{
    [Fact]
    public async Task Empty_keyword_configuration_is_neutral_and_not_described_as_a_baseline()
    {
        var provider = new KeywordScoringProvider(Options.Create(new ScoringOptions()));
        var article = new ArticleCandidate("1", "Any ordinary article", "https://example.test/1", null, null, DateTimeOffset.UtcNow);

        var result = await provider.ScoreAsync(article);

        Assert.Equal(0.5, result.Value, precision: 5);
        Assert.Equal("No user-defined keyword preferences are configured.", result.Reason);
        Assert.DoesNotContain("baseline", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Positive_keyword_raises_score()
    {
        var provider = new KeywordScoringProvider(Options.Create(new ScoringOptions { BaseScore = 0.5, KeywordWeight = 0.2, PositiveKeywords = ["dotnet"] }));
        var article = new ArticleCandidate("1", "A practical dotnet service", "https://example.test/1", null, null, DateTimeOffset.UtcNow);
        var result = await provider.ScoreAsync(article);
        Assert.Equal(0.7, result.Value, precision: 5);
    }

    [Fact]
    public async Task Score_is_clamped_to_valid_range()
    {
        var provider = new KeywordScoringProvider(Options.Create(new ScoringOptions { BaseScore = 0.9, KeywordWeight = 0.5, PositiveKeywords = ["rss", "reader"] }));
        var article = new ArticleCandidate("1", "RSS reader", "https://example.test/1", null, null, DateTimeOffset.UtcNow);
        var result = await provider.ScoreAsync(article);
        Assert.Equal(1, result.Value);
    }
}
