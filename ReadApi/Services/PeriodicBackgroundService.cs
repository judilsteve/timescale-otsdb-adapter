using System.Diagnostics;

namespace TimescaleOpenTsdbAdapter.Services;

public abstract class PeriodicBackgroundService<T>(
    ILogger<T> logger,
    TimeSpan interval,
    TimeSpan timeout,
    double jitterFactor = 0
) : BackgroundService
{
    protected readonly ILogger<T> logger = logger;
    private readonly TimeSpan interval = interval;
    private readonly TimeSpan timeout = timeout;
    private readonly double jitterFactor = jitterFactor;

    protected abstract Task RunPeriodicTask(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (timeout > interval)
        {
            logger.LogWarning(
                "Timeout ({timeout}) exceeds interval ({interval})",
                timeout, interval
            );
        }

        using var timer = new PeriodicTimer(interval);
        var stopwatch = new Stopwatch();

        try
        {
            do
            {
                await Task.Delay(jitterFactor * interval * Random.Shared.NextDouble(), cancellationToken);

                stopwatch.Restart();

                try
                {
                    await RunPeriodicTask(cancellationToken);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Periodic task failed! Will try again next interval");
                }

                stopwatch.Stop();

                if (stopwatch.Elapsed > interval)
                {
                    logger.LogWarning(
                        "Periodic task is falling behind! Last update took {lastUpdateTime} but interval is only {interval}",
                        stopwatch.Elapsed, interval
                    );
                }

            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Periodic task is stopping.");
        }
    }
}
