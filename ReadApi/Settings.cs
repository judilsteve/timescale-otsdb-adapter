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
        $"Host={GetEnv("TIMESCALE_HOST")};Port={GetEnv("TIMESCALE_PORT")};Username={GetEnv("TIMESCALE_USER")};Password={GetEnv("TIMESCALE_PASSWORD")};Database={GetEnv("TIMESCALE_DBNAME")};IncludeErrorDetail={isDebugBuild}";

    public static readonly TimeSpan TagsetCacheUpdateInterval = GetEnv("TAGSET_CACHE_UPDATE_INTERVAL_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(30));
    public static readonly TimeSpan TagsetCacheUpdateTimeout = GetEnv("TAGSET_CACHE_UPDATE_TIMEOUT_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(10));

    // This must match or exceed Timescale's configured retention policy, or old tagsets/metrics will become unqueryable!
    public static readonly TimeSpan? DataRetentionPeriod = GetEnv<TimeSpan?>(
        "DATA_RETENTION_DAYS",
        s => TimeSpan.FromDays(double.Parse(s)),
        null
    );

    public static readonly TimeSpan? HousekeepingInterval = GetEnv<TimeSpan?>(
        "HOUSEKEEPING_INTERVAL_SECONDS",
        s => TimeSpan.FromSeconds(double.Parse(s)),
        // No point running housekeeping if we never drop old data
        DataRetentionPeriod.HasValue ? TimeSpan.FromHours(1) : null
    );
    public static readonly TimeSpan HousekeepingTimeout = GetEnv("HOUSEKEEPING_TIMEOUT_SECONDS", s => TimeSpan.FromSeconds(double.Parse(s)), TimeSpan.FromSeconds(120));
}
