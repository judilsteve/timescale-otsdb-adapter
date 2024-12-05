namespace TimescaleOpenTsdbAdapter;

public static class Settings
{

#if DEBUG
    private const bool isDebugBuild = true;
#else
    private const bool isDebugBuild = false;
#endif

    private static string GetEnv(string key, string? fallback = null)
    {
        var envvar = Environment.GetEnvironmentVariable(key);
        return envvar ?? fallback ?? throw new Exception($"Missing required envvar {key}");
    }

    private static T GetEnv<T>(string key, Func<string, T> parser, T fallback)
    {
        var envvar = Environment.GetEnvironmentVariable(key);
        if(envvar is not null) return parser(envvar);
        else return fallback;
    }

    public static readonly string TimescaleConnectionString =
        $"Host={GetEnv("TIMESCALE_HOST")};Port={GetEnv("TIMESCALE_PORT", "5432")};Username={GetEnv("TIMESCALE_USER", "postgres")};Password={GetEnv("TIMESCALE_PASSWORD")};Database={GetEnv("TIMESCALE_DBNAME", "postgres")};IncludeErrorDetail={isDebugBuild}";

    // How often to update the tagset cache that is used to process queries
    public static readonly TimeSpan TagsetCacheUpdateInterval = GetEnv("TAGSET_CACHE_UPDATE_INTERVAL_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(30));
    public static readonly TimeSpan TagsetCacheUpdateTimeout = GetEnv("TAGSET_CACHE_UPDATE_TIMEOUT_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(10));

    // Tagsets/metrics/tsids older than this with no data-points associated will be deleted by the housekeeping task.
    // This must match or exceed Timescale's configured retention policy, or old tagsets/metrics will become unqueryable!
    public static readonly TimeSpan? DataRetentionPeriod = GetEnv<TimeSpan?>(
        "DATA_RETENTION_DAYS",
        s => TimeSpan.FromDays(double.Parse(s)),
        null
    );

    // How often to prune unused tagsets/metrics/tsuids
    // Since Timescale drops data in complete hypertable chunks,
    // this should be larger than or equal to the chunk size.
    public static readonly TimeSpan? HousekeepingInterval = GetEnv<TimeSpan?>(
        "HOUSEKEEPING_INTERVAL_SECONDS",
        s => TimeSpan.FromSeconds(double.Parse(s)),
        // No point running housekeeping if we never drop old data
        DataRetentionPeriod.HasValue ? TimeSpan.FromHours(1) : null
    );
    public static readonly TimeSpan HousekeepingTimeout = GetEnv("HOUSEKEEPING_TIMEOUT_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(120));

    // Maximum size of caches used for looking up metric/tagset IDs when inserting data-points
    public static readonly int MetricCacheSize = GetEnv("INSERT_METRIC_CACHE_SIZE", int.Parse, 65536);
    public static readonly int TagsetCacheSize = GetEnv("INSERT_TAGSET_CACHE_SIZE", int.Parse, 2097152);

    // IMPORTANT: This must be significantly *less* than Timescale's data-point retention time,
    // or else we might insert data-points with cached metric/tagset IDs that no longer exist in the DB!
    public static readonly TimeSpan? CacheEntryTtl = DataRetentionPeriod.HasValue ? DataRetentionPeriod.Value / 2 : null;

}
