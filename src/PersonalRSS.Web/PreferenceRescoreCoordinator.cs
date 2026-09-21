using System.Threading.Channels;
using PersonalRSS.Application;

public sealed record PreferenceRescoreStatus(
    long Generation,
    string State,
    int? Updated,
    string? Error,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? CompletedAt);

public sealed class PreferenceRescoreCoordinator(
    IServiceScopeFactory scopeFactory,
    ILogger<PreferenceRescoreCoordinator> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Channel<long> _requests = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private PreferenceRescoreStatus _status = new(0, "idle", null, null, null, null);
    private CancellationTokenSource? _activeRequest;
    private long _generation;

    public PreferenceRescoreStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public PreferenceRescoreStatus Request()
    {
        PreferenceRescoreStatus status;
        lock (_gate)
        {
            var generation = ++_generation;
            _activeRequest?.Cancel();
            status = new PreferenceRescoreStatus(generation, "queued", null, null, DateTimeOffset.UtcNow, null);
            _status = status;
            _requests.Writer.TryWrite(generation);
        }
        return status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(stoppingToken))
            {
                var generation = 0L;
                while (_requests.Reader.TryRead(out var queuedGeneration)) generation = queuedGeneration;

                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_gate)
                {
                    if (generation != _generation) continue;
                    _activeRequest = requestCancellation;
                    _status = _status with { State = "running", Error = null };
                }

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var rescoring = scope.ServiceProvider.GetRequiredService<PreferenceRescoringService>();
                    var updated = await rescoring.RescoreAsync(requestCancellation.Token);
                    lock (_gate)
                    {
                        if (generation == _generation)
                            _status = _status with
                            {
                                State = "completed",
                                Updated = updated,
                                CompletedAt = DateTimeOffset.UtcNow
                            };
                    }
                }
                catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
                {
                    // A newer request superseded this one, or the application is stopping.
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Background relevance recalculation failed.");
                    lock (_gate)
                    {
                        if (generation == _generation)
                            _status = _status with
                            {
                                State = "failed",
                                Error = "Relevance could not be recalculated.",
                                CompletedAt = DateTimeOffset.UtcNow
                            };
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeRequest, requestCancellation)) _activeRequest = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }
}
