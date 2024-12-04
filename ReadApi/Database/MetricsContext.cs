using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using TimescaleOpenTsdbAdapter.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace TimescaleOpenTsdbAdapter.Database;

public class MetricsContext(DbContextOptions<MetricsContext> options) : DbContext(options)
{
    public DbSet<Metric> Metric { get; set; }
    public DbSet<Tagset> Tagset { get; set; }
    public DbSet<Point> Point { get; set; }
    public DbSet<TimeSeries> TimeSeries { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseNpgsql(Settings.TimescaleConnectionString);
        options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        // The "suggest" endpoint returns a limited number of rows, without any particular
        // ordering, which would by default emit a warning in the logs. We silence it here.
        options.ConfigureWarnings(w => w.Ignore(CoreEventId.RowLimitingOperationWithoutOrderByWarning));
    }

    public IQueryable<LastValue> GetLastValues(IEnumerable<string> metricNames, IEnumerable<int> tagsetIds, DateTimeOffset? asAt = null)
    {
        // Hand-written SQL here as this query is quite performance sensitive
        // Derived from solution 2a described here: https://stackoverflow.com/a/25536748
        // Note: Safe against SQL injection, see https://learn.microsoft.com/en-us/ef/core/querying/sql-queries#passing-parameters
        return Database.SqlQuery<LastValue>(@$"
            select m.name as metric, ts.tagset_id, latest_p.time, latest_p.value
            from time_series ts
            cross join lateral (
                select p.time, p.value
                from point p
                where p.metric_id = ts.metric_id
                and p.tagset_id = ts.tagset_id
                and ({asAt}::timestamp with time zone is null or p.time <= {asAt}::timestamp with time zone)
                order by p.time desc
                limit 1
            ) latest_p
            join metric m on m.id = ts.metric_id
            where m.name = any({metricNames})"
        ).Where(lv => tagsetIds.Contains(lv.TagsetId));
    }

    // TODO Archive these before deleting?
    public async Task<(int, int, int)> PruneOrphanedTimeSeries(CancellationToken cancellationToken)
    {
        if(!Settings.DataRetentionPeriod.HasValue) throw new Exception("No data retention period configured, nothing to prune!");

        // We do these in batches to avoid timeouts if housekeeping hasn't been run for a while
        var deletedSeries = 0;
        int deletedInBatch;
        do
        {
            // Hand-written SQL here as this query is quite performance sensitive
            deletedInBatch = await Database.ExecuteSqlAsync(@$"
                delete from time_series
                where (metric_id, tagset_id) in
                (
                    select metric_id, tagset_id from time_series ts
                    left join lateral (
                        select 1 as point_exists
                        from point p
                        where p.metric_id = ts.metric_id
                        and p.tagset_id = ts.tagset_id
                        limit 1
                    ) p on true
                    where now() - ts.last_used > {Settings.DataRetentionPeriod.Value}
                    and p.point_exists is null
                    limit 1000
                )",
                cancellationToken
            );
            deletedSeries += deletedInBatch;
        } while (deletedInBatch > 0);

        // Now that the time_series table has been pruned, we can
        // leverage it to quickly prune orphaned metrics/tagsets

        // IMPORTANT: The metric streamer maintains a cache of metric/tagset IDs,
        // so we must be careful here. The metric streamer is set up to evict cache
        // entries that it has not written a data-point for in a while (where "a while"
        // is hopefully less than the data-point retention period), but there is still
        // a possible race condition:
        //
        // 1. Streamer adds some metrics/tagsets
        // 2. We prune metrics/tagsets (including the ones just added by streamer)
        // 3. Streamer unwittingly caches orphaned tagset/metric IDs, and uses them to
        //    insert points.
        //
        // To avoid the above scenario, and as a general safeguard (e.g. if we manually
        // delete data for some reason), we do not delete recently created metrics/tagsets,
        // where "recently created" means "within the current retention window."

        var deletedMetrics = 0;
        do
        {
            deletedInBatch = await Metric
                .Where(m => DateTimeOffset.UtcNow - m.Created > Settings.DataRetentionPeriod.Value)
                .Where(m => !TimeSeries.Any(ts => ts.MetricId == m.Id))
                .Take(1000)
                .ExecuteDeleteAsync(cancellationToken);
            deletedMetrics += deletedInBatch;
        } while (deletedInBatch > 0);

        var deletedTagsets = 0;
        do
        {
            deletedInBatch = await Tagset
                .Where(t => DateTimeOffset.UtcNow - t.Created > Settings.DataRetentionPeriod.Value)
                .Where(t => !TimeSeries.Any(ts => ts.TagsetId == t.Id))
                .Take(1000)
                .ExecuteDeleteAsync(cancellationToken);
            deletedTagsets += deletedInBatch;
        } while (deletedInBatch > 0);

        return (deletedSeries, deletedMetrics, deletedTagsets);
    }

    public IQueryable<DownsampledPoint> DownsampledPoints(Downsample downsample, DateTimeOffset start, DateTimeOffset? end)
    {
        // time_bucket_gapfill will *throw* if the time column is not constrained at both ends,
        // so we cannot use "or @end is null" in our SQL. Instead, we default to the current time.
        // If we chose to set end to the future, time_bucket_gapfill would return null-valued rows
        // for all time buckets in the future part of the range, and we don't want that either.
        end ??= DateTimeOffset.UtcNow;

        string bucketingExpression;
        // Special case for "0all" downsampling. Bit of a magic value, but until C# gets
        // union types, the alternative is adding a sentinel field to Downsample (yuck).
        if(downsample.Bucket == TimeSpan.MaxValue)
        {
            // Use the start timestamp for all points (matches OTSDB)
            bucketingExpression = "{1}";
        }
        else
        {
            // See https://docs.timescale.com/api/latest/hyperfunctions/gapfilling/time_bucket_gapfill/
            var bucketFunction = downsample.FillPolicy == FillPolicy.None ? "time_bucket" : "time_bucket_gapfill";
            bucketingExpression = $"{bucketFunction}({{0}}, p.time)";

            // When downsampling, OTSDB aligns the time range to the bucket boundaries (shifting right),
            // which avoids the issue of underfull buckets at each end of the query range.
            start = start.AddTicks(downsample.Bucket.Ticks - (start.Ticks % downsample.Bucket.Ticks));
            end = end.Value.AddTicks(downsample.Bucket.Ticks - (end.Value.Ticks % downsample.Bucket.Ticks));
        }

        var aggregateExpression = downsample.Function switch
        {
            AggregatorFunction.Count => "count(1)",
            AggregatorFunction.First => "first(p.value, p.time)",
            AggregatorFunction.Last => "last(p.value, p.time)",
            AggregatorFunction.Max => "max(p.value)",
            AggregatorFunction.Min => "min(p.value)",
            AggregatorFunction.Mean => "avg(p.value)",
            // There is a function in the "toolkit" that can do fast percentile approximations, may be worth using
            // here once timescaledb-ha docker image (which includes toolkit) is available with postgres 17 tags:
            // https://docs.timescale.com/api/latest/hyperfunctions/percentile-approximation/uddsketch/#percentile_agg
            // For now, use postgres' exact version
            AggregatorFunction.Median => "percentile_cont(0.5) within group(order by p.value)",
            AggregatorFunction.Sum => "sum(p.value)",
            _ => throw new ArgumentException($"Unsupported downsample aggregator {downsample.Function}", nameof(downsample))
        };

        // We must do this with handcrafted SQL, since EF will split time_bucket_gapfill and the
        // group by/where clauses that it relies on into a subquery and out query, respectively.
        // See https://stackoverflow.com/questions/76065611/entity-framework-avoid-subquery-created-by-groupby
        # pragma warning disable EF1002
        // NOTE: Be very careful modifying this query, as it is vulnerable to SQL injection:
        // Standard parameterisation (double curlies) should be used for most values, and
        // raw string interpolation (single curlies) should be reserved for function names,
        // column names, and other identifiers.
        var query = Database.SqlQueryRaw<DownsampledPoint>(@$"
            select
                p.metric_id,
                p.tagset_id,
                {bucketingExpression} as time_bucket,
                {aggregateExpression} as value
            from point p
            -- time_bucket_gapfill will *throw* if we don't constrain time at both ends
            where p.time >= {{1}}
            and p.time < {{2}}
            group by p.metric_id, p.tagset_id, time_bucket
        ", downsample.Bucket, start, end);
        # pragma warning restore EF1002

        return query.Select(p => new DownsampledPoint
        {
            MetricId = p.MetricId,
            TagsetId = p.TagsetId,
            TimeBucket = p.TimeBucket,
            // time_bucket_gapfill always fills with nulls, we must generate zero/NaN ourselves as required
            Value = downsample.FillPolicy == FillPolicy.Zero ? (p.Value ?? 0) : downsample.FillPolicy == FillPolicy.NaN ? (p.Value ?? double.NaN) : p.Value
        });
    }
}

public class DownsampledMetricPoint
{
    [Column("tagset_id")] public int TagsetId { get; init; }
    [Column("time_bucket")] public DateTimeOffset TimeBucket { get; init; }
    [Column("value")] public double? Value { get; init; }
}

public class DownsampledPoint : DownsampledMetricPoint
{
    [Column("metric_id")] public int MetricId { get; init; }
}

public class LastValue
{
    [Column("metric")] public required string Metric { get; set; }
    [Column("tagset_id")] public int TagsetId { get; set; }
    [Column("time")] public DateTimeOffset Time { get; set; }
    [Column("value")] public double Value { get; set; }
}

[Table("point")]
[PrimaryKey(nameof(TagsetId), nameof(MetricId), nameof(Time))]
public class Point
{
    [Column("tagset_id")] public int TagsetId { get; set; }
    public required Tagset Tagset { get; set; }
    [Column("metric_id")] public short MetricId { get; set; }
    public required Metric Metric { get; set; }
    [Column("time")] public DateTimeOffset Time { get; set; }
    [Column("value")] public double Value { get; set; }
}

[Table("metric")]
[PrimaryKey(nameof(Id))]
public class Metric
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")] public short Id { get; set; }
    [Column("name")] public required string Name { get; set; }
    [Column("created")] public DateTimeOffset Created { get; set; }
}

[Table("tagset")]
[PrimaryKey(nameof(Id))]
public class Tagset
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")] public int Id { get; set; }
    [Column("tags")] public required JsonDocument Tags { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("created")] public DateTimeOffset Created { get; set; }
}

[Table("time_series")]
[PrimaryKey(nameof(MetricId), nameof(TagsetId))]
public class TimeSeries
{
    [Column("metric_id")] public short MetricId { get; set; }
    public required Metric Metric { get; set; }
    [Column("tagset_id")] public int TagsetId { get; set; }
    public required Tagset Tagset { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("created")] public DateTimeOffset Created { get; set; }
}
