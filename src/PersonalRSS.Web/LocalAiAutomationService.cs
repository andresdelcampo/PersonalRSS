using PersonalRSS.Application;
using PersonalRSS.Infrastructure;

public sealed record LocalAiAutomationStatus(
    string State,
    string Message,
    int Reviewed,
    int Accepted,
    int Neutral,
    int Failed,
    DateTimeOffset? LastActivityAt);

public sealed class LocalAiAutomationService(
    IServiceScopeFactory scopeFactory,
    ILogger<LocalAiAutomationService> logger) : BackgroundService
{
    private LocalAiAutomationStatus _status = new("starting", "Starting automatic Local AI review…", 0, 0, 0, 0, null);
    public LocalAiAutomationStatus Status => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IFeedRepository>();
                var settings = await repository.GetLocalAiSettingsAsync(stoppingToken);
                if (!settings.Enabled)
                {
                    _status = _status with { State = "disabled", Message = "Automatic review is switched off." };
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                var review = scope.ServiceProvider.GetRequiredService<LocalAiReviewService>();
                _status = _status with { State = "reviewing", Message = "Checking for an unread post to review…" };
                var result = await review.ReviewUnreadAsync(1, stoppingToken);
                var now = result.Reviewed + result.Failed > 0 ? DateTimeOffset.UtcNow : _status.LastActivityAt;
                _status = new LocalAiAutomationStatus(
                    result.Remaining > 0 ? "reviewing" : "idle",
                    Message(result),
                    _status.Reviewed + result.Reviewed,
                    _status.Accepted + result.Accepted,
                    _status.Neutral + result.Neutral,
                    _status.Failed + result.Failed,
                    now);

                var delay = result.Remaining == 0
                    ? TimeSpan.FromSeconds(10)
                    : result.Failed > 0 && result.Reviewed == 0
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromMilliseconds(500);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Automatic Local AI review paused after an error.");
                _status = _status with { State = "waiting", Message = "Automatic review is waiting for LM Studio." };
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private static string Message(LocalAiReviewResult result)
    {
        if (result.Reviewed == 0 && result.Failed == 0) return "All unread, unrated posts have been reviewed.";
        if (result.Failed > 0 && result.Reviewed == 0) return "LM Studio is unavailable or returned an unusable response; retrying later.";
        if (result.Accepted > 0) return "Accepted a strong semantic match; continuing in the background.";
        if (result.Neutral > 0) return "Kept the local score after a neutral assessment; continuing in the background.";
        return "Continuing automatic Local AI review in the background.";
    }
}
