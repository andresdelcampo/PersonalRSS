using Microsoft.Extensions.Options;
using PersonalRSS.Application;
using PersonalRSS.Core;

namespace PersonalRSS.Infrastructure;

public sealed class ScoringOptions
{
    public const string SectionName = "Scoring";
    public double BaseScore { get; set; } = 0.5;
    public double KeywordWeight { get; set; } = 0.12;
    public string[] PositiveKeywords { get; set; } = [];
    public string[] NegativeKeywords { get; set; } = [];
    public PreferenceLearningOptions PreferenceLearning { get; set; } = new();
}

public sealed class KeywordScoringProvider(IOptions<ScoringOptions> options) : IScoringProvider
{
    private readonly ScoringOptions _options = options.Value;
    public string Name => "keyword-baseline";

    public Task<ScoreResult> ScoreAsync(ArticleCandidate article, CancellationToken cancellationToken = default)
    {
        var text = $"{article.Title} {article.Summary}";
        var positive = _options.PositiveKeywords.Count(keyword => Contains(text, keyword));
        var negative = _options.NegativeKeywords.Count(keyword => Contains(text, keyword));
        var value = Math.Clamp(_options.BaseScore + (positive - negative) * _options.KeywordWeight, 0, 1);
        var hasConfiguredKeywords = _options.PositiveKeywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword)) ||
                                    _options.NegativeKeywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword));
        var reason = hasConfiguredKeywords
            ? $"User keyword starting score {_options.BaseScore:0.00}; {positive} positive and {negative} negative keyword matches."
            : "No user-defined keyword preferences are configured.";
        return Task.FromResult(new ScoreResult(value, reason));
    }

    private static bool Contains(string text, string keyword) => !string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
