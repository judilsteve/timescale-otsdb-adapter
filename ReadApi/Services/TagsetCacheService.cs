using TimescaleOpenTsdbAdapter.Database;
using TimescaleOpenTsdbAdapter.Dtos;
using TimescaleOpenTsdbAdapter.TagFilters;
using TimescaleOpenTsdbAdapter.Utils;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimescaleOpenTsdbAdapter.Services;

/// <summary>
/// This is an unbounded cache, designed to store *all* known tagsets. Since tagsets are immutable,
/// refreshing the cache is as simple as running a query to fetch any tagsets created since the
/// last refresh. Using an unbounded cache means that we never have to worry about cache misses,
/// so long as we update the cache frequently enough. The pitfall of the unbounded cache is that
/// if we ever have too many tagsets, we will run out of RAM.
/// </summary>
public class TagsetCacheService(IDbContextFactory<MetricsContext> dbContextFactory, ILogger<TagsetCacheService> logger)
    : PeriodicBackgroundService<TagsetCacheService>(logger, Settings.TagsetCacheUpdateInterval, Settings.TagsetCacheUpdateTimeout)
{
    // NOTE: We use the factory to get a handle on a dbContext here because
    // TagsetCacheService is a singleton and DbContexts are scoped: I.e. the lifespan
    // of the cache service exceeds that of the DbContext, so we cannot constructor-inject
    // a single DbContext instance and use it throughout the lifecycle of this service
    // This should NOT be copy/pasted as a general pattern for DB access throughout the application
    private readonly IDbContextFactory<MetricsContext> dbContextFactory = dbContextFactory;

    private readonly SemaphoreSlim updateLock = new(1, 1);

    // Maps tagset ID -> tagset
    // Using frozen dictionaries here massively decreases memory use
    private readonly ConcurrentDictionary<int, FrozenDictionary<string, string>> tagsetLookup = [];

    // Maps metric name -> tagset IDs
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<int>> timeSeriesLookup = [];

    private readonly TagIndex tagIndex = new();

    // This stores the creation date of the newest tagset added during the last periodic "full" update
    private DateTimeOffset tagsetLowWaterMark = DateTimeOffset.MinValue;
    // This stores the creation date of the newest time series added during the last periodic "full" update
    private DateTimeOffset timeSeriesLowWaterMark = DateTimeOffset.MinValue;

    public DateTimeOffset? LastSuccessfulUpdate { get; private set; } = null;

    protected override async Task RunPeriodicTask(CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Do this in order so we can save progress incrementally in case we time out
            var newTagsets = context.Tagset
                .Where(t => t.Created > tagsetLowWaterMark)
                .Select(t => new { t.Id, t.Tags, t.Created })
                .OrderBy(t => t.Created);

            await foreach (var tagset in newTagsets.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var frozenTags = tagset.Tags.Deserialize<Dictionary<string, string>>()!.ToFrozenDictionary();
                tagsetLookup[tagset.Id] = frozenTags;

                foreach (var (tagk, tagv) in frozenTags)
                {
                    tagIndex.AddTag(tagk, tagv, tagset.Id);
                }

                tagsetLowWaterMark = tagset.Created;
            }

            var newTimeSeries = context.TimeSeries
                .Where(ts => ts.Created > timeSeriesLowWaterMark)
                .Select(ts => new { MetricName = ts.Metric.Name, ts.TagsetId, ts.Created })
                .OrderBy(ts => ts.Created);

            await foreach (var timeSeries in newTimeSeries.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var metricName = timeSeries.MetricName;
                if (!timeSeriesLookup.TryGetValue(metricName, out var tagsetIds))
                {
                    tagsetIds = [];
                    timeSeriesLookup[metricName] = tagsetIds;
                }

                tagsetIds.Add(timeSeries.TagsetId);

                timeSeriesLowWaterMark = timeSeries.Created;
            }

            LastSuccessfulUpdate = DateTimeOffset.UtcNow;
        }
        finally
        {
            updateLock.Release();
        }
    }

    public async Task Prune(CancellationToken cancellationToken)
    {
        if(!LastSuccessfulUpdate.HasValue) return; // Nothing to prune
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var activeTagsetIds = context.Tagset
                .Select(t => t.Id)
                .ToFrozenSet();

            var orphanedTagsets = tagsetLookup
                .Where(kvp => !activeTagsetIds.Contains(kvp.Key))
                .ToArray();

            if(orphanedTagsets.Length == 0) return;

            foreach(var (orphanedTagsetId, orphanedTagset) in orphanedTagsets)
            {
                tagsetLookup.Remove(orphanedTagsetId, out _);
                // Since we are probably removing many tagsets, it will be faster to skip pruning
                // tag values for each tagset removed, and instead just rebuild the index from
                // scratch after all our pruning is complete
                tagIndex.RemoveTagset(orphanedTagsetId, orphanedTagset, pruneTagValues: false);
            }
            tagIndex.RebuildTagValues();
        }
        finally
        {
            updateLock.Release();
        }
    }

    private static Regex WildcardToRegex(string wildcardPattern, bool caseSensitive)
    {
        var options = RegexOptions.Compiled;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(Regex.Escape(wildcardPattern).Replace("\\*", ".*"), options);
    }

    private static HashSet<int> GetTagsetIdsMatchingFilter(
        QueryFilterDto filterDto,
        ConcurrentDictionary<string, ConcurrentHashSet<int>> tagValueIndex
    ) {
        var tagsetIdsMatchingThisFilter = new HashSet<int>();
        switch (filterDto.Type)
        {
            case FilterType.LiteralOr:
                foreach (var allowedTagValue in filterDto.Filter.Split('|'))
                {
                    if (tagValueIndex.TryGetValue(allowedTagValue, out var allowedTagsetIds))
                        tagsetIdsMatchingThisFilter.UnionWith(allowedTagsetIds);
                };
                break;
            case FilterType.CaseInsensitiveLiteralOr:
                // This is not ideal, because it requires enumerating the entire list of possible tag values for this tag key
                // We could speed this up by maintaining a second, case-insensitive version of tagIndex, but it doesn't seem
                // worth the extra code complexity, considering that it will only speed up very niche use-cases
                var filterValues = filterDto.Filter.ToLowerInvariant().Split('|').ToHashSet();
                foreach (var (tagValue, tagValuetagsetIds) in tagValueIndex)
                {
                    if (filterValues.Contains(tagValue.ToLowerInvariant())) tagsetIdsMatchingThisFilter.UnionWith(tagValuetagsetIds);
                }
                break;
            case FilterType.NotLiteralOr:
                var filterValuesToExclude = filterDto.Filter.Split('|').ToHashSet();
                foreach (var (tagValue, tagValueTagsetIds) in tagValueIndex)
                {
                    if (!filterValuesToExclude.Contains(tagValue)) tagsetIdsMatchingThisFilter.UnionWith(tagValueTagsetIds);
                }
                break;
            case FilterType.CaseInsensitiveNotLiteralOr:
                var caseInsensitiveFilterValuesToExclude = filterDto.Filter.ToLowerInvariant().Split('|').ToHashSet();
                foreach (var (tagValue, tagValueTagsetIds) in tagValueIndex)
                {
                    if (!caseInsensitiveFilterValuesToExclude.Contains(tagValue.ToLowerInvariant())) tagsetIdsMatchingThisFilter.UnionWith(tagValueTagsetIds);
                }
                break;
            case FilterType.Wildcard:
                var wildcardRegex = WildcardToRegex(filterDto.Filter, caseSensitive: true);
                foreach (var (tagValue, tagValueTagsetIds) in tagValueIndex)
                {
                    if (wildcardRegex.IsMatch(tagValue)) tagsetIdsMatchingThisFilter.UnionWith(tagValueTagsetIds);
                }
                break;
            case FilterType.CaseInsensitiveWildcard:
                var caseInsensitiveWildcardRegex = WildcardToRegex(filterDto.Filter, caseSensitive: false);
                foreach (var (tagValue, tagValueTagsetIds) in tagValueIndex)
                {
                    if (caseInsensitiveWildcardRegex.IsMatch(tagValue)) tagsetIdsMatchingThisFilter.UnionWith(tagValueTagsetIds);
                }
                break;
            case FilterType.RegularExpression:
                var regex = new Regex(filterDto.Filter, RegexOptions.Compiled);
                foreach (var (tagValue, tagValueTagsetIds) in tagValueIndex)
                {
                    if (regex.IsMatch(tagValue)) tagsetIdsMatchingThisFilter.UnionWith(tagValueTagsetIds);
                }
                break;
            default:
                throw new NotImplementedException($"Filter type {filterDto.Type} not implemented!");
        }

        return tagsetIdsMatchingThisFilter;
    }

    private static readonly IReadOnlyDictionary<int, FrozenDictionary<string, string>> emptyLookup = new Dictionary<int, FrozenDictionary<string, string>>();

    public IReadOnlyDictionary<int, FrozenDictionary<string, string>> GetTagsets(
        IEnumerable<string> metrics,
        IList<QueryFilterDto> filters,
        bool explicitTags
    ) {
        if(!LastSuccessfulUpdate.HasValue) throw new Exception("Tagset cache not initialised yet!");

        if (filters.Count == 0) return tagsetLookup;

        // Run filters with fewest possible tag values first, as an
        // heuristic to help reduce the matching tagset count quickly
        var orderedFilters = filters.OrderBy(f => tagIndex.GetPossibleTagValueCount(f.Tagk));

        HashSet<int>? tagsetIds = null;
        foreach(var metric in metrics)
        {
            if(timeSeriesLookup.TryGetValue(metric, out var tagsetIdsForMetric))
            {
                if(tagsetIds is null) tagsetIds = [.. tagsetIdsForMetric];
                else tagsetIds.UnionWith(tagsetIdsForMetric);
            }
        }

        if(tagsetIds is null) return emptyLookup; // No point going any further

        if(explicitTags)
        {
            var uniqueTagKeys = filters.Select(f => f.Tagk).ToFrozenSet();
            tagsetIds.RemoveWhere(tagsetId =>
                !tagsetLookup.TryGetValue(tagsetId, out var tagset) ||
                !uniqueTagKeys.SetEquals(tagset.Keys)
            );
        }

        foreach (var filterDto in orderedFilters)
        {
            // Note that OTSDB doesn't honour case-insensitivity for tag keys, only tag values
            if (!tagIndex.TryGetTagValueIndex(filterDto.Tagk, out var tagValueIndex) || tagValueIndex.IsEmpty)
            {
                // This tag key doesn't exist at all, so nothing will match.
                // And before you ask: "What about a wildcard/not filter?"
                // Real OTSDB will throw an error if you supply a non-existent tag
                // key in a query, so us returning an empty result is an upgrade.
                return emptyLookup;
            }

            // Dynamically choosing between these two approaches for evaluating filters
            // results in a 4-10x performance improvement for many of our slowest scenarios
            if(filterDto.Type != FilterType.LiteralOr && tagValueIndex.Count > tagsetIds.Count)
            {
                var filter = TagFilterUtils.CreateTagFilter(filterDto);
                var tagk = filterDto.Tagk;
                // Faster to iterate through tagsetIds and remove those which don't match the filter
                tagsetIds.RemoveWhere(t =>
                    !tagsetLookup.TryGetValue(t, out var tagset) ||
                    !tagset.TryGetValue(tagk, out var tagv) ||
                    !filter.IsMatch(tagv)
                );
            }
            else
            {
                // Faster to search through tagValueIndex to find tagsetIdsMatchingThisFilter and then intersect with tagsetIds
                tagsetIds = FastIntersect(tagsetIds, GetTagsetIdsMatchingFilter(filterDto, tagValueIndex));
            }

            if (tagsetIds.Count == 0) return emptyLookup; // No point going any further
        }

        return tagsetIds!.ToDictionary(tid => tid, tid => tagsetLookup[tid]);
    }

    // Optimised version of Intersect that works by *modifying* one of the input collections
    private static HashSet<T> FastIntersect<T>(HashSet<T> a, HashSet<T> b)
    {
        var smaller = a.Count < b.Count ? a : b;
        var larger = b.Count < a.Count ? a : b;
        smaller.RemoveWhere(e => !larger.Contains(e));
        return smaller;
    }

    public IEnumerable<string> GetMetricNames() => timeSeriesLookup.Keys;
    public IEnumerable<string> GetTagKeys() => tagIndex.GetTagKeys();
    public IEnumerable<string> GetTagValues() => tagIndex.GetTagValues();
    public IEnumerable<string> GetTagValues(string tagKey) => tagIndex.GetTagValues(tagKey);
    public IEnumerable<int> GetTagsetIdsByTagValues(params string[] tagValues) => tagIndex.GetTagsetIdsByTagValue(tagValues);

    public IEnumerable<string> GetTagKeys(string metric)
    {
        if(!timeSeriesLookup.TryGetValue(metric, out var tagsetIds)) yield break;

        var seenTagKeys = new HashSet<string>();
        foreach(var tagsetId in tagsetIds)
        {
            if(!tagsetLookup.TryGetValue(tagsetId, out var tagset)) continue;
            foreach(var tagKey in tagset.Keys)
            {
                if(seenTagKeys.Contains(tagKey)) continue;
                yield return tagKey;
                seenTagKeys.Add(tagKey);
            }
        }
    }

    public FrozenDictionary<string, string>? GetTagsetOrDefault(int tagsetId)
    {
        return tagsetLookup.GetValueOrDefault(tagsetId);
    }

    private class TagIndex
    {
        // Maps tagk -> tagv -> [tagset IDs containing this tag pair]
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentHashSet<int>>> keyIndex = new();

        // Set of all possible tag values
        private ConcurrentHashSet<string> allTagValues = [];

        public void AddTag(string tagKey, string tagValue, int tagsetId)
        {
            allTagValues.Add(tagValue);

            if (!keyIndex.TryGetValue(tagKey, out var tagValueIndex))
            {
                tagValueIndex = [];
                keyIndex[tagKey] = tagValueIndex;
            }

            if (!tagValueIndex.TryGetValue(tagValue, out var tagsetIds))
            {
                tagsetIds = [];
                tagValueIndex[tagValue] = tagsetIds;
            }

            tagsetIds.Add(tagsetId);
        }

        public void RemoveTagset(int tagsetId, FrozenDictionary<string, string> tagset, bool pruneTagValues = true)
        {
            foreach(var (tagk, tagv) in tagset)
            {
                if (!keyIndex.TryGetValue(tagk, out var valueIndex)) return;
                if (!valueIndex.TryGetValue(tagv, out var tagsetIds)) return;

                tagsetIds.Remove(tagsetId);

                if (tagsetIds.Count == 0)
                {
                    valueIndex.Remove(tagv, out _);

                    if(valueIndex.IsEmpty)
                    {
                        keyIndex.Remove(tagk, out _);
                    }

                    if (pruneTagValues)
                    {
                        var tagvUsedElsewhere = keyIndex.Values.Any(valueIndex => valueIndex.ContainsKey(tagv));
                        if(!tagvUsedElsewhere) allTagValues.Remove(tagv);
                    }
                }
            }
        }

        public void RebuildTagValues()
        {
            allTagValues = [..keyIndex.Values.SelectMany(valueIndex => valueIndex.Keys)];
        }

        public bool TryGetTagValueIndex(string tagKey, out ConcurrentDictionary<string, ConcurrentHashSet<int>> tagValueIndex)
        {
            return keyIndex.TryGetValue(tagKey, out tagValueIndex!);
        }

        public int GetPossibleTagValueCount(string tagKey)
        {
            return keyIndex.GetValueOrDefault(tagKey)?.Count ?? 0;
        }

        public IEnumerable<string> GetTagKeys() => keyIndex.Keys;
        public IEnumerable<string> GetTagValues() => allTagValues;

        public IEnumerable<string> GetTagValues(string tagKey) => keyIndex.TryGetValue(tagKey, out var valueIndex) ? valueIndex.Keys : [];

        public IEnumerable<int> GetTagsetIdsByTagValue(string[] tagValues)
        {
            foreach(var tagValueIndex in keyIndex.Values)
            {
                foreach(var tagValue in tagValues)
                {
                    if(!tagValueIndex.TryGetValue(tagValue, out var tagsetIds)) continue;
                    foreach(var tagsetId in tagsetIds) yield return tagsetId;
                }
            }
        }
    }
}
