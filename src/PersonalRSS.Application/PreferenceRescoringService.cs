using PersonalRSS.Core;

namespace PersonalRSS.Application;

public sealed class PreferenceRescoringService(IFeedRepository repository, IScoringProvider scoringProvider)
{
    public async Task<int> RescoreAsync(CancellationToken cancellationToken = default)
    {
        var stored = await repository.GetArticlesForRescoringAsync(cancellationToken);
        if (stored.Count == 0) return 0;
        var candidates = stored.Select(item => item.Candidate).ToList();
        IReadOnlyList<ScoreResult> scores = scoringProvider is IBatchScoringProvider batchScoringProvider
            ? await batchScoringProvider.ScoreAsync(candidates, cancellationToken)
            : await Task.WhenAll(candidates.Select(candidate => scoringProvider.ScoreAsync(candidate, cancellationToken)));
        var updates = new List<AutomaticScoreUpdate>(stored.Count);
        for (var index = 0; index < stored.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = scores[index];
            updates.Add(new AutomaticScoreUpdate(
                stored[index].ArticleId,
                Math.Clamp(score.BaselineValue ?? score.Value, 0, 1),
                score.BaselineReason ?? score.Reason,
                Math.Clamp(score.Value, 0, 1),
                score.Reason,
                Math.Clamp(score.Confidence, 0, 1),
                Math.Max(0, score.MatchingFeedbackCount),
                Math.Max(0, score.PositiveEvidence),
                Math.Max(0, score.NegativeEvidence),
                score.ConfidenceReason));
        }
        return await repository.UpdateAutomaticScoresAsync(updates, cancellationToken);
    }
}
