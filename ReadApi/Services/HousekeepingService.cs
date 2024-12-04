using TimescaleOpenTsdbAdapter.Database;
using Microsoft.EntityFrameworkCore;

namespace TimescaleOpenTsdbAdapter.Services;

public class HousekeepingService(
    IDbContextFactory<MetricsContext> dbContextFactory,
    ILogger<HousekeepingService> logger,
    TagsetCacheService tagsetCache,
    IWebHostEnvironment environment
) : PeriodicBackgroundService<HousekeepingService>(logger, Settings.HousekeepingInterval!.Value, Settings.HousekeepingTimeout, jitterFactor: 0.2)
{
    private readonly IDbContextFactory<MetricsContext> dbContextFactory = dbContextFactory;
    private readonly TagsetCacheService tagsetCache = tagsetCache;
    private readonly IWebHostEnvironment environment = environment;

    protected override async Task RunPeriodicTask(CancellationToken cancellationToken)
    {
        if(!environment.IsDevelopment())
        {
            // NOTE: We use the factory to get a handle on a dbContext here because
            // HousekeepingService is a singleton and DbContexts are scoped: I.e. the lifespan
            // of the cache service exceeds that of the DbContext, so we cannot constructor-inject
            // a single DbContext instance and use it throughout the lifecycle of this service
            // This should NOT be copy/pasted as a general pattern for DB access throughout the application
            using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Ideally this would be run only on a single instance instead of being duplicated across
            // all of them, but since it takes <1s to run in the case where there is nothing to delete,
            // we can get away with duplicating the job. Adding jitter to the start time also helps
            // prevent overlapping pruning jobs on instances that were started around the same time.
            await context.PruneOrphanedTimeSeries(cancellationToken);
        }

        await tagsetCache.Prune(cancellationToken);
    }
}
