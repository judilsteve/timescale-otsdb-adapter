using TimescaleOpenTsdbAdapter.Aggregators;
using TimescaleOpenTsdbAdapter.Database;
using TimescaleOpenTsdbAdapter.Dtos;
using TimescaleOpenTsdbAdapter.Services;
using TimescaleOpenTsdbAdapter.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TimescaleOpenTsdbAdapter.Controllers;

[ApiController]
[Route("api/query")]
public partial class QueryController(
   MetricsContext context,
   TagsetCacheService tagsetCacheService
) : ControllerBase
{
    private readonly MetricsContext context = context;
    private readonly TagsetCacheService tagsetCacheService = tagsetCacheService;

    private class TagValues(params string[] values) : IComparable<TagValues>
    {
        private readonly string[] values = values;

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in values) hash.Add(value);
            return hash.ToHashCode();
        }

        public override bool Equals(object? obj)
        {
            if (obj is not TagValues other) return false;
            return values.SequenceEqual(other.values);
        }

        public int CompareTo(TagValues? other)
        {
            if (other is null) return 1;

            for (int i = 0; i < values.Length; i++)
            {
                var compareResult = values[i].CompareTo(other.values[i]);
                if (compareResult != 0) return compareResult;
            }
            return 0;
        }
    }

    private class TagsetGroup
    {
        public readonly List<int> TagsetIds = [];

        // All tag keys that are present in every tagset in the group and have the *same* values
        public readonly Dictionary<string, string> Tags = [];

        public void Add(int tagsetId, FrozenDictionary<string, string> tagset)
        {
            if (TagsetIds.Count > 0)
            {
                foreach (var (key, value) in tagset)
                {
                    if (Tags.TryGetValue(key, out var existingValue) && existingValue != value)
                    {
                        Tags.Remove(key);
                    }
                }
            }
            else
            {
                foreach (var (key, value) in tagset)
                {
                    Tags[key] = value;
                }
            }
            TagsetIds.Add(tagsetId);
        }
    }

    private static IAggregator CreateAggregator(AggregatorFunction agg)
    {
        return agg switch
        {
            AggregatorFunction.Mean => new MeanAggregator(),
            AggregatorFunction.Median => new MedianAggregator(),
            AggregatorFunction.Count => new CountAggregator(),
            AggregatorFunction.Min => new MinAggregator(),
            AggregatorFunction.Max => new MaxAggregator(),
            AggregatorFunction.Sum => new SumAggregator(),
            AggregatorFunction.First => new FirstAggregator(),
            AggregatorFunction.Last => new LastAggregator(),
            _ => throw new ArgumentException($"Unhandled aggregator function {agg}", nameof(agg))
        };
    }

    [HttpPost("last")]
    public async IAsyncEnumerable<LastQueryResultDto> Last(LastQueryDto query)
    {
        foreach (var queryPart in query.Queries)
        {
            var filters = ConvertTagsToFilters(queryPart.Tags ?? []).ToArray();
            var metrics = queryPart.Metrics ?? [queryPart.Metric!];
            var tagsets = tagsetCacheService.GetTagsets(metrics, filters, explicitTags: false);

            var lastPoints = context.GetLastValues(metrics, tagsets.Keys, query.AsAt);

            await foreach (var point in lastPoints.AsAsyncEnumerable().WithCancellation(HttpContext.RequestAborted))
            {
                yield return new LastQueryResultDto
                {
                    // If the wire becomes a bottleneck for these queries,
                    // we may want to consider returning just metric_id and then looking up the name from a cache
                    Metric = point.Metric,
                    Tags = tagsets[point.TagsetId],
                    Value = point.Value,
                    Timestamp = point.Time.ToUnixTimeMilliseconds()
                };
            }
        }
    }

    private static IRateConverter MakeRateConverter(DateTimeOffset queryStart, QueryPartDto.QueryRateOptions? rateOptions)
    {
        if(rateOptions?.Counter ?? false)
        {
            return new CountRateConverter(queryStart, rateOptions.CounterMax, rateOptions.DropResets);
        }
        return new PlainRateConverter(queryStart);
    }

    [HttpPost]
    public async IAsyncEnumerable<QueryResultDto> Query(QueryDto query)
    {
        var i = 0;
        foreach (var queryPart in query.Queries)
        {
            var filters = queryPart.Filters ?? Enumerable.Empty<QueryFilterDto>();
            if(queryPart.Tags is not null) filters = filters.Concat(ConvertTagsToFilters(queryPart.Tags));
            var filtersArray = filters.ToArray();

            var tagsets = tagsetCacheService.GetTagsets([queryPart.Metric], filtersArray, queryPart.ExplicitTags ?? false);

            if(tagsets.Count == 0) continue;

            var dbQuery = GetQuery(query, queryPart, tagsets);

            if (queryPart.Aggregator.HasValue && queryPart.Aggregator.Value != AggregatorFunction.None)
            {
                var groupedResults = await RunAggregatedQuery(filtersArray, queryPart.Aggregator.Value, tagsets, dbQuery);

                foreach (var (tagsetGroup, result) in groupedResults)
                {
                    IEnumerable<(long, double?)> dps;
                    if (queryPart.Rate ?? false)
                    {
                        var rateConverter = MakeRateConverter(query.Start!.Value, queryPart.RateOptions);
                        dps = rateConverter.CalcRateChanges(result, x => x.Key, x => x.Value.Result);
                    }
                    else
                    {
                        dps = result
                            .Select(kvp => (kvp.Key.ToUnixTimeSeconds(), kvp.Value.Result));
                    }

                    yield return new QueryResultDto
                    {
                        Query = new QueryResultDto.QueryIdentifierDto { Index = i },
                        Metric = queryPart.Metric,
                        Tags = tagsetGroup.Tags,
                        Dps = dps.ToArray()
                    };
                };
            }
            else
            {
                var segmented = dbQuery
                    .OrderBy(r => r.TagsetId)
                    .ThenBy(r => r.TimeBucket)
                    .AsAsyncEnumerable()
                    .SegmentBy(p => p.TagsetId)
                    .WithCancellation(HttpContext.RequestAborted);

                await foreach(var (tagsetId, results) in segmented)
                {
                    var nextResult = new List<(long timestamp, double? value)>();
                    var rateConverter = (queryPart.Rate ?? false) ? MakeRateConverter(query.Start!.Value, queryPart.RateOptions) : null;
                    await foreach(var result in results)
                    {
                        if(rateConverter is not null)
                        {
                            if(rateConverter.TryCalcRateChange(result.TimeBucket, result.Value, out var rateChange))
                            {
                                nextResult.Add((result.TimeBucket.ToUnixTimeSeconds(), rateChange));
                            }
                        }
                        else
                        {
                            nextResult.Add((result.TimeBucket.ToUnixTimeSeconds(), result.Value));
                        }
                    }

                    yield return new QueryResultDto
                    {
                        Query =  new QueryResultDto.QueryIdentifierDto { Index = i },
                        Metric = queryPart.Metric,
                        Tags = tagsets[tagsetId],
                        Dps = nextResult
                    };
                }
            }
            i++;
        }
    }

    private async Task<Dictionary<TagsetGroup, SortedDictionary<DateTimeOffset, IAggregator>>> RunAggregatedQuery(
        IEnumerable<QueryFilterDto> filters,
        AggregatorFunction aggregatorFunction,
        IReadOnlyDictionary<int, FrozenDictionary<string, string>> tagsets,
        IQueryable<DownsampledMetricPoint> downsampled
    )
    {
        var tagsetGroupLookup = new TagsetGroupLookup(filters, tagsets);
        var groupedResults = new Dictionary<TagsetGroup, SortedDictionary<DateTimeOffset, IAggregator>>();

        await foreach(var point in downsampled.AsAsyncEnumerable().WithCancellation(HttpContext.RequestAborted))
        {
            var tagsetGroup = tagsetGroupLookup.GetOrCreateTagsetGroup(point.TagsetId);
            if (!groupedResults.TryGetValue(tagsetGroup, out var result))
            {
                result = [];
                groupedResults[tagsetGroup] = result;
            }

            if (!result.TryGetValue(point.TimeBucket, out var aggregator))
            {
                aggregator = CreateAggregator(aggregatorFunction);
                result[point.TimeBucket] = aggregator;
            }

            aggregator.AddNext(point.Value);
        }

        return groupedResults;
    }

    private IQueryable<DownsampledMetricPoint> GetQuery(
        QueryDto query,
        QueryPartDto queryPart,
        IReadOnlyDictionary<int, FrozenDictionary<string, string>> tagsets
    ) {
        var start = query.Start!.Value;

        // OTSDB widens the query range when doing rate queries, so that the first point
        // within the time range can actually  have a meaningful value (rate is always
        // relative to the previous datapoint). This is a bit of a kludge, but the "proper"
        // solution of fetching the latest prior timestamp for each individual time series
        // would be exceedingly complicated, and we barely use rate queries anyway.
        if(queryPart.Rate ?? false) start = start.AddHours(-1);

        IQueryable<DownsampledPoint> points;
        if(queryPart.Downsample is not null)
        {
            points = context.DownsampledPoints(queryPart.Downsample, start, query.End);
        }
        else
        {
            var pointsForTimeRange = context.Point
                // OTSDB range is inclusive at both ends
                .Where(p => p.Time >= start);

            if (query.End.HasValue)
            {
                // OTSDB range is inclusive at both ends
                pointsForTimeRange = pointsForTimeRange.Where(p => p.Time <= query.End.Value);
            }

            points = pointsForTimeRange
                .Select(p => new DownsampledPoint
                {
                    MetricId = p.MetricId,
                    TagsetId = p.TagsetId,
                    TimeBucket = p.Time,
                    Value = p.Value
                });
        }

        return points
            .Where(p => p.MetricId == context.Metric.First(m => m.Name == queryPart.Metric).Id)
            .Where(p => tagsets.Keys.Contains(p.TagsetId))
            .Select(p => new DownsampledMetricPoint
            {
                TagsetId = p.TagsetId,
                TimeBucket = p.TimeBucket,
                Value = p.Value
            });
    }

    private class TagsetGroupLookup(
        IEnumerable<QueryFilterDto> filters,
        IReadOnlyDictionary<int, FrozenDictionary<string, string>> tagsetIdToTagset
    ) {
        public string[] GroupingTagKeys { get; private init; } = filters
            .Where(f => f.GroupBy)
            .Select(f => f.Tagk)
            .Distinct()
            .ToArray();

        private readonly Dictionary<TagValues, TagsetGroup> tagValuesToTagsetGroup = [];
        private readonly Dictionary<int, TagsetGroup> tagsetIdToTagsetGroup = [];
        private readonly IReadOnlyDictionary<int, FrozenDictionary<string, string>> tagsetIdToTagset = tagsetIdToTagset;

        public TagsetGroup GetOrCreateTagsetGroup(int tagsetId)
        {
            if(tagsetIdToTagsetGroup.TryGetValue(tagsetId, out var tagsetGroup))
                return tagsetGroup;

            var tagset = tagsetIdToTagset[tagsetId];

            var tagValuesArray = new string[GroupingTagKeys.Length];
            for (var t = 0; t < tagValuesArray.Length; t++)
            {
                tagValuesArray[t] = tagset[GroupingTagKeys[t]];
            }
            var tagValues = new TagValues(tagValuesArray);

            if (!tagValuesToTagsetGroup.TryGetValue(tagValues, out tagsetGroup))
            {
                tagsetGroup = new TagsetGroup();
                tagValuesToTagsetGroup[tagValues] = tagsetGroup;
            }

            tagsetGroup.Add(tagsetId, tagset);
            tagsetIdToTagsetGroup[tagsetId] = tagsetGroup;

            return tagsetGroup;
        }
    }

    [GeneratedRegex(@"^(((not_)?i?literal_or|i?wildcard)|regexp)\((.*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex LongFormTagFilterRegex();
    private static readonly Regex longFormTagFilterRegex = LongFormTagFilterRegex();

    private static IEnumerable<QueryFilterDto> ConvertTagsToFilters(IReadOnlyDictionary<string, string> tags)
    {
        // See http://opentsdb.net/docs/build/html/api_http/query/index.html#filter-conversions
        foreach (var (key, value) in tags)
        {
            var match = longFormTagFilterRegex.Match(value);
            FilterType type;
            string filterValue;
            if(match.Success)
            {
                type = FilterTypeConverter.Parse(match.Groups[1].Value);
                filterValue = match.Groups[4].Value;
            }
            else
            {
                type = value.Contains('*') ? FilterType.CaseInsensitiveWildcard : FilterType.LiteralOr;
                filterValue = value;
            }

            yield return new QueryFilterDto
            {
                Type = type,
                Tagk = key,
                Filter = filterValue,
                GroupBy = true
            };
        }
    }
}

public class QueryDto
{
    [JsonConverter(typeof(OtsdbTimeSpecConverter))]
    [Required]
    public DateTimeOffset? Start { get; init; }

    [JsonConverter(typeof(OtsdbTimeSpecConverter))]
    public DateTimeOffset? End { get; init; }

    [MinLength(1)]
    public required List<QueryPartDto> Queries { get; init; }
}

public class LastQueryDto
{
    [JsonConverter(typeof(OtsdbTimeSpecConverter))]
    public DateTimeOffset? AsAt { get; init; }

    [MinLength(1)]
    public required List<LastQueryPartDto> Queries { get; init; }
}

public class LastQueryResultDto
{
    public required string Metric { get; init; }
    public long Timestamp { get; init; } // UNIX timestamp in millis
    public double Value { get; init; }
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
}

// See http://opentsdb.net/docs/build/html/user_guide/query/dates.html
public partial class OtsdbTimeSpecConverter : JsonConverter<DateTimeOffset>
{
    public static TimeSpan ParseTimeSpan(ReadOnlySpan<char> quantityString, ReadOnlySpan<char> unit)
    {
        var quantity = double.Parse(quantityString);

        return unit switch
        {
            "all" => TimeSpan.MaxValue,
            "ms" => TimeSpan.FromMilliseconds(quantity),
            "s" => TimeSpan.FromSeconds(quantity),
            "m" => TimeSpan.FromMinutes(quantity),
            "h" => TimeSpan.FromHours(quantity),
            "d" => TimeSpan.FromDays(quantity),
            "w" => TimeSpan.FromDays(quantity * 7),
            "n" => TimeSpan.FromDays(quantity * 30),
            "y" => TimeSpan.FromDays(quantity * 365),
            _ => throw new ArgumentException($"Unrecognised unit {unit}", nameof(unit))
        };
    }

    [GeneratedRegex(@"^(\d+\.*\d*)(all|ms|s|m|h|d|w|n|y)-ago$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeTimeRegex();
    private static readonly Regex relativeTimeRegex = RelativeTimeRegex();

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var timeString = reader.GetString()!;

                if(timeString == "now") return DateTimeOffset.UtcNow;

                var relativeTimeMatch = relativeTimeRegex.Match(timeString);
                if (relativeTimeMatch.Success)
                {
                    var timeSpan = ParseTimeSpan(relativeTimeMatch.Groups[1].ValueSpan, relativeTimeMatch.Groups[2].ValueSpan);
                    return DateTimeOffset.UtcNow.Subtract(timeSpan);
                }

                try
                {
                    var timeLong = long.Parse(timeString);

                    if(timeLong < 10_000_000_000)
                    {
                        return DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(timeLong);
                    }
                    else
                    {
                        // Assume millis if more than 10 digits (first 11-digit seconds-precision UNIX timestamp is in the year 2286)
                        return DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(timeLong);
                    }
                }
                catch
                {
                    return DateTimeOffset.Parse(timeString, null, DateTimeStyles.AssumeUniversal);
                }
            case JsonTokenType.Number:
                var epochQuantity = reader.GetDouble();
                if (epochQuantity != Math.Floor(epochQuantity) || epochQuantity < 10_000_000_000)
                {
                    // Always interpret fractional values as seconds, or assume seconds if 10 digits or less
                    return DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(epochQuantity);
                }
                else
                {
                    // Assume millis if more than 10 digits (first 11-digit seconds-precision UNIX timestamp is in the year 2286)
                    return DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(epochQuantity);
                }
            default:
                throw new JsonException($"Invalid token type {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        // Placeholder implementation, just enough to generate an example payload for the swagger UI
        writer.WriteStringValue("1h-ago");
    }
}

public class LastQueryPartDto : IValidatableObject
{
    public string? Metric { get; init; }
    public string[]? Metrics { get; init; }
    public Dictionary<string, string>? Tags { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(Metric))
        {
            if ((Metrics?.Length ?? 0) == 0)
                yield return new ValidationResult($"At least one of {nameof(Metric)} or {nameof(Metrics)} must be provided", [nameof(Metric), nameof(Metrics)]);
        }
        else
        {
            if (Metrics?.Length > 0)
                yield return new ValidationResult($"Only one of {nameof(Metric)} or {nameof(Metrics)} can be provided", [nameof(Metric), nameof(Metrics)]);
        }
    }
}

public class QueryPartDto : IValidatableObject
{
    // See http://opentsdb.net/docs/build/html/api_http/query/index.html#rate-options
    public class QueryRateOptions
    {
        public bool Counter { get; init; } = false;
        public long CounterMax { get; init; } = long.MaxValue;
        public bool DropResets { get; init; } = false;
    }

    public required string Metric { get; init; }
    public Dictionary<string, string>? Tags { get; init; }
    public AggregatorFunction? Aggregator { get; init; }
    public bool? Rate { get; init; }
    public QueryRateOptions? RateOptions { get; init; }
    public Downsample? Downsample { get; init; }
    public List<QueryFilterDto>? Filters { get; init; }
    public bool? ExplicitTags { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((ExplicitTags ?? false) && (Filters?.Count ?? 0) + (Tags?.Count ?? 0) == 0)
            yield return new ValidationResult("At least one tag/filter must be specified to use explicitTags", [nameof(ExplicitTags), nameof(Filters), nameof(Tags)]);
    }
}

[JsonConverter(typeof(AggregatorFunctionConverter))]
public enum AggregatorFunction
{
    Mean,
    Median,
    Count,
    First,
    Last,
    Min,
    Max,
    Sum,
    None
}

public class AggregatorFunctionConverter : JsonConverter<AggregatorFunction>
{
    public static AggregatorFunction ParseFunction(ReadOnlySpan<char> functionString)
    {
        return functionString switch
        {
            "avg" => AggregatorFunction.Mean,
            "median" => AggregatorFunction.Median,
            "count" => AggregatorFunction.Count,
            "first" => AggregatorFunction.First,
            "last" => AggregatorFunction.Last,
            "min" => AggregatorFunction.Min,
            "max" => AggregatorFunction.Max,
            "sum" => AggregatorFunction.Sum,
            "none" => AggregatorFunction.None,
            "all" => AggregatorFunction.None,
            _ => throw new ArgumentException($"Unrecognised aggregator function \"{functionString}\"", nameof(functionString))
        };
    }

    public override AggregatorFunction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Invalid token type {reader.TokenType}");
        return ParseFunction(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, AggregatorFunction value, JsonSerializerOptions options)
    {
        // Placeholder implementation, just enough to generate an example payload for the swagger UI
        writer.WriteStringValue("avg");
    }
}

public enum FillPolicy
{
    None,
    NaN,
    Null,
    Zero
}

[JsonConverter(typeof(DownsampleConverter))]
public record Downsample(TimeSpan Bucket, AggregatorFunction Function, FillPolicy FillPolicy);
public partial class DownsampleConverter : JsonConverter<Downsample>
{
    [GeneratedRegex(@"^(\d+\.*\d*)(all|ms|s|m|h|d|w|n|y)-([a-z]+)(-([a-z]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DownsampleRegex();
    private static readonly Regex downsampleRegex = DownsampleRegex();

    public override Downsample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Invalid token type {reader.TokenType}");

        var downsampleString = reader.GetString()!;

        var regexMatch = downsampleRegex.Match(downsampleString);

        if (!regexMatch.Success) throw new JsonException($"Malformed downsample \"{downsampleString}\"");

        var bucket = OtsdbTimeSpecConverter.ParseTimeSpan(regexMatch.Groups[1].ValueSpan, regexMatch.Groups[2].ValueSpan);

        var function = AggregatorFunctionConverter.ParseFunction(regexMatch.Groups[3].ValueSpan);

        FillPolicy fillPolicy = FillPolicy.None;
        if (regexMatch.Groups.Count > 5)
        {
            var fp = regexMatch.Groups[5].ValueSpan;
            if(!fp.IsEmpty) {
                fillPolicy = Enum.Parse<FillPolicy>(fp, ignoreCase: true);
            }
        }

        return new Downsample(bucket, function, fillPolicy);
    }

    public override void Write(Utf8JsonWriter writer, Downsample value, JsonSerializerOptions options)
    {
        // Placeholder implementation, just enough to generate an example payload for the swagger UI
        writer.WriteStringValue("5m-avg-none");
    }
}

public class QueryResultDto
{
    public class QueryIdentifierDto
    {
        public int Index { get; init; }
    }

    public required string Metric { get; init; }
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
    public string[] AggregateTags { get; } = []; // Note: We don't use this, so we don't bother populating it
    [JsonConverter(typeof(TupleToDictionaryConverter))] public required IList<(long, double?)> Dps { get; init; }
    public required QueryIdentifierDto Query { get; init; }
}

public class TupleToDictionaryConverter : JsonConverter<IList<(long, double?)>>
{
    public override IList<(long, double?)> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotImplementedException();

    public override void Write(Utf8JsonWriter writer, IList<(long, double?)> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k.ToString()!);
            if(v.HasValue)
            {
                if(!double.IsNaN(v.Value)) writer.WriteNumberValue(v.Value);
                else writer.WriteStringValue("NaN");
            }
            else
            {
                writer.WriteNullValue();
            }
        }
        writer.WriteEndObject();
    }
}
