using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TimescaleOpenTsdbAdapter.Utils;

namespace TimescaleOpenTsdbAdapter.Controllers;

[ApiController]
[Route("api/put")]
public class InsertController(ILogger<InsertController> logger, NpgsqlDataSource dataSource) : ControllerBase
{
    // IMPORTANT: Old data-points are periodically deleted from Timescale, along with any metrics
    // or tagsets that are no longer in use after deleting the old data-points. Therefore we must
    // enforce strict TTL on these cache entries, so that we don't keep a metric/tagset ID in the
    // cache after it has been deleted by the cleanup jobs, and accidentally post data-points with
    // non-existent metric/tagset IDs
    // TODO Prune these at the same time as the query pipeline's tagset cache?
    private static readonly ITtlLruCache<string, long> metricIdCache = Settings.CacheEntryTtl.HasValue
        ? new TtlLruCache<string, long>(Settings.MetricCacheSize, Settings.CacheEntryTtl.Value)
        : new NoTtlLruCache<string, long>(Settings.MetricCacheSize);
    private static readonly ITtlLruCache<TagsetDto, long> tagsetIdCache = Settings.CacheEntryTtl.HasValue
        ? new TtlLruCache<TagsetDto, long>(Settings.TagsetCacheSize, Settings.CacheEntryTtl.Value)
        : new NoTtlLruCache<TagsetDto, long>(Settings.TagsetCacheSize);

    private readonly ILogger<InsertController> logger = logger;
    private readonly NpgsqlDataSource dataSource = dataSource;

    public class DataPointDto
    {
        public required string Metric { get; init; }
        [Required] public long? Timestamp { get; init; }
        [Required] public double? Value { get; init; }
        public required TagsetDto Tags { get; init; }
    }

    [JsonConverter(typeof(TagsetDtoJsonConverter))]
    public class TagsetDto : IComparable<TagsetDto>
    {
        public TagsetDto(Dictionary<string, string> tags)
        {
            Tags = tags
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToArray();
            Array.Sort(Tags);
            // Since we know this will *always* be checked (to lookup tagset ID), we precompute the value
            hashCode = ComputeHashCode();
        }

        public readonly (string tagK, string tagV)[] Tags;
        private readonly int hashCode;

        private int ComputeHashCode()
        {
            var hash = new HashCode();

            foreach (var (key, value) in Tags)
            {
                hash.Add(key);
                hash.Add(value);
            }

            return hash.ToHashCode();
        }

        public override int GetHashCode() => hashCode;

        public override bool Equals(object? obj)
        {
            if (obj is not TagsetDto other) return false;
            return Tags.SequenceEqual(other.Tags);
        }

        public int CompareTo(TagsetDto? other)
        {
            if (other is null) return 1;

            var lengthDiff = Tags.Length - other.Tags.Length;
            if (lengthDiff != 0) return lengthDiff;

            for (int i = 0; i < Tags.Length; i++)
            {
                var compareResult = Tags[i].CompareTo(other.Tags[i]);
                if (compareResult != 0) return compareResult;
            }

            return 0;
        }

        public string GetTagValueOrDefault(string tagK, string defaultValue)
        {
            // Array.BinarySearch could do this for us, but this avoids declaring a custom IComparer
            var low = 0;
            var high = Tags.Length - 1;
            while (low <= high) {
                var mid = low + (high - low) / 2;

                var (testTagK, tagV) = Tags[mid];

                var comparisonResult = string.CompareOrdinal(tagK, testTagK);
                if (comparisonResult == 0)
                    return tagV;
                else if (comparisonResult > 0)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            return defaultValue;
        }
    }

    public class TagsetDtoJsonConverter : JsonConverter<TagsetDto>
    {
        public override TagsetDto Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
                new(JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader)!);

        public override void Write(Utf8JsonWriter writer, TagsetDto value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (k, v) in value.Tags)
            {
                writer.WritePropertyName(k);
                writer.WriteStringValue(v);
            }
            writer.WriteEndObject();
        }
    }

    async Task<long[]> ResolveMetricIds(string[] metricNames)
    {
        // Fastest way to insert many rows in PostGreSQL (except for COPY FROM, which doesn't allow conflict resoluton):
        // https://github.com/npgsql/npgsql/issues/2779#issuecomment-573439342
        // "on conflict do update" incurs a write lock, but using "on conflict do nothing" here means
        // that existing rows won't return an id. See https://stackoverflow.com/a/57886410
        await using var command = dataSource.CreateCommand(@"
            insert into metric (name)
            select * from unnest(@metric_names::text[])
            on conflict (name) do update set exists=true
            returning id
        ");
        command.Parameters.Add(new NpgsqlParameter<string[]>("metric_names", metricNames));

        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
        var result = new long[metricNames.Length];
        var i = 0;
        while (await reader.ReadAsync(HttpContext.RequestAborted)) result[i++] = await reader.GetFieldValueAsync<long>(0, HttpContext.RequestAborted);
        return result;
    }

    async Task<long[]> ResolveTagsetIds(TagsetDto[] tagsets)
    {
        // Fastest way to insert many rows in PostGreSQL (except for COPY FROM, which doesn't allow conflict resoluton):
        // https://github.com/npgsql/npgsql/issues/2779#issuecomment-573439342
        // "on conflict do update" incurs a write lock, but using "on conflict do nothing" here means
        // that existing rows won't return an id. See https://stackoverflow.com/a/57886410
        await using var command = dataSource.CreateCommand(@"
            insert into tagset (tags)
            select * from unnest(@tags::jsonb[])
            on conflict (tags) do update set exists=true
            returning id
        ");
        var tags = tagsets.Select(ts => JsonSerializer.Serialize(ts)).ToArray();
        command.Parameters.Add(new NpgsqlParameter<string[]>("tags", tags));

        await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
        var result = new long[tagsets.Length];
        var i = 0;
        while (await reader.ReadAsync(HttpContext.RequestAborted)) result[i++] = await reader.GetFieldValueAsync<long>(0, HttpContext.RequestAborted);
        return result;
    }


    // See https://opentsdb.net/docs/build/html/api_http/put.html
    [HttpPost]
    public async Task<ActionResult> InsertPoints(DataPointDto[] points)
    {
        var stopwatch = Stopwatch.StartNew();

        // First pass: gather required metrics/tsids
        var uncachedMetrics = new HashSet<string>();
        var uncachedTagsets = new HashSet<TagsetDto>();
        // We copy the IDs from the main LRUCache into our own micro-cache
        // to avoid the situation where cached values drop out of the LRU
        // while we're looking up the uncached values in the DB
        var metricIds = new Dictionary<string, long>();
        var tagsetIds = new Dictionary<TagsetDto, long>();

        var oldestTimestampForBatch = long.MaxValue;
        foreach (var point in points)
        {
            var metric = point.Metric;

            if (!metricIdCache.TryGetValue(metric, out var metricId))
            {
                uncachedMetrics.Add(metric);
            }
            else
            {
                metricIds[metric] = metricId;
            }

            var tags = point.Tags;
            if (!tagsetIdCache.TryGetValue(tags, out var tagsetId))
            {
                uncachedTagsets.Add(tags);
            }
            else
            {
                tagsetIds[tags] = tagsetId;
            }

            if(point.Timestamp < oldestTimestampForBatch)
            {
                oldestTimestampForBatch = point.Timestamp!.Value;
            }
        }

        if (uncachedMetrics.Count != 0)
        {
            var sortedUncachedMetrics = uncachedMetrics.OrderBy(m => m).ToArray(); // Ordering is important to prevent deadlocks
            var resolvedMetricIds = await ResolveMetricIds(sortedUncachedMetrics);
            foreach (var (metricName, metricId) in Enumerable.Zip(sortedUncachedMetrics, resolvedMetricIds))
            {
                metricIds[metricName] = metricId;
            }
        }

        if (uncachedTagsets.Count != 0)
        {
            var sortedUncachedTagsets = uncachedTagsets.OrderBy(ts => ts).ToArray(); // Ordering is important to prevent deadlocks
            var resolvedTagsetIds = await ResolveTagsetIds(sortedUncachedTagsets);
            foreach (var (tagset, tagsetId) in Enumerable.Zip(sortedUncachedTagsets, resolvedTagsetIds))
            {
                tagsetIds[tagset] = tagsetId;
            }
        }

        // Second pass: Insert data-points
        var (batchMetricIds, batchTagsetIds, batchTimestamps, batchValues) =
            (new long[points.Length], new long[points.Length], new long[points.Length], new double[points.Length]);
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            batchMetricIds[i] = metricIds[point.Metric];
            batchTagsetIds[i] = tagsetIds[point.Tags];
            batchTimestamps[i] = point.Timestamp!.Value;
            batchValues[i] = point.Value!.Value;
        }

        // Fastest way to insert many rows in PostGreSQL (except for COPY FROM, which doesn't allow conflict resoluton):
        // https://github.com/npgsql/npgsql/issues/2779#issuecomment-573439342
        await using var command = dataSource.CreateCommand(@"
            insert into point (metric_id, tagset_id, time, value)
            select
                m_id, ts_id, TO_TIMESTAMP(t), v
            from unnest(@metric_ids, @tagset_ids, @times, @values)
            as inputs(m_id, ts_id, t, v)
            order by m_id, ts_id, t -- Ordering is important to prevent deadlocks
            ON CONFLICT (metric_id, tagset_id, time) DO NOTHING -- First write wins
        ");
        command.Parameters.Add(new NpgsqlParameter<long[]>("metric_ids", batchMetricIds));
        command.Parameters.Add(new NpgsqlParameter<long[]>("tagset_ids", batchTagsetIds));
        command.Parameters.Add(new NpgsqlParameter<long[]>("times", batchTimestamps));
        command.Parameters.Add(new NpgsqlParameter<double[]>("values", batchValues));

        await command.ExecuteNonQueryAsync(HttpContext.RequestAborted);

        // After we have successfully inserted these data-points, we can refresh the TTL
        // on the metric and tagset IDs used, since we can be sure they will be retained
        // at least until the data-points just inserted fall out of the retention window
        var conservativeCurrentAsAtEstimate = DateTimeOffset.FromUnixTimeSeconds(oldestTimestampForBatch).UtcDateTime;
        foreach (var (metricName, metricId) in metricIds)
        {
            metricIdCache.AddOrRevalidate(metricName, metricId, conservativeCurrentAsAtEstimate);
        }
        foreach (var (tagset, tagsetId) in tagsetIds)
        {
            tagsetIdCache.AddOrRevalidate(tagset, tagsetId, conservativeCurrentAsAtEstimate);
        }

        return Ok(new {
            PointsWritten = points.Length,
            WriteTimeMillis = stopwatch.ElapsedMilliseconds,
            Kdps = points.Length / 1000d / stopwatch.Elapsed.TotalSeconds,
            UncachedMetrics = uncachedMetrics.Count,
            TotalMetrics = metricIds.Count,
            MetricIdMissRate = uncachedMetrics.Count / (double)metricIds.Count,
            UncachedTagsets = uncachedTagsets.Count,
            TotalTagsets = tagsetIds.Count,
            TagsetIdMissRate = uncachedTagsets.Count / (double)tagsetIds.Count
        });
    }
}
