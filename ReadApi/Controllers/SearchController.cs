using Microsoft.AspNetCore.Mvc;
using TimescaleOpenTsdbAdapter.Services;
using System.ComponentModel.DataAnnotations;
using TimescaleOpenTsdbAdapter.Database;
using Microsoft.EntityFrameworkCore;

namespace TimescaleOpenTsdbAdapter.Controllers;

[ApiController]
[Route("api/search")]
public class SearchController(
    TagsetCacheService tagsetCacheService,
    MetricsContext context
) : ControllerBase
{
    private readonly TagsetCacheService tagsetCacheService = tagsetCacheService;
    private readonly MetricsContext context = context;

    public class LookupRequestDto : IValidatableObject
    {
        public class LookupRequestTagDto
        {
            public required string Key { get; init; }
            public required string Value { get; init; }
        }

        public List<LookupRequestTagDto>? Tags { get; init; }
        public string? Metric { get; init; }
        public int? Limit { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if ((Tags?.Count ?? 0) == 0 && Metric is null)
                yield return new ValidationResult("At least one of metric or tags must be provided.", [nameof(Tags), nameof(Metric)]);

            var tagKeysWithWildcards = new HashSet<string>();
            foreach(var tag in Tags ?? [])
            {
                if(tag.Key == "*" && tag.Value == "*")
                    yield return new ValidationResult("Only one of tag key/value can be a wildcard.", [nameof(Tags)]);
                else if(tag.Value == "*")
                    tagKeysWithWildcards.Add(tag.Key);
                else if(tagKeysWithWildcards.Contains(tag.Key))
                    yield return new ValidationResult($"Cannot mix wildcard and literal values on the same tag key (\"{tag.Key}\").", [nameof(Tags)]);
            }
        }
    }

    public class LookupResponseDto
    {
        public class LookupResultDto
        {
            public required string Metric { get; init; }
            public required IReadOnlyDictionary<string, string> Tags { get; init; }
        }

        public required IAsyncEnumerable<LookupResultDto> Results { get; init; }
        public int TotalResults { get; init; }
    }

    [HttpPost("lookup")]
    public async Task<LookupResponseDto> Search(LookupRequestDto request)
    {
        // TODO Use time series/tagset cache for this?

        var query = context.TimeSeries.AsQueryable();

        if(request.Metric is not null && request.Metric != "*")
        {
            query = query.Where(s => s.Metric.Name == request.Metric);
        }

        var tagValuesByKey = (request.Tags ?? [])
            .GroupBy(t => t.Key)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Value).ToArray());

        foreach(var (tagKey, tagValues) in tagValuesByKey)
        {
            if(tagKey == "*")
            {
                var tagsetIds = tagsetCacheService.GetTagsetIdsByTagValues(tagValues);
                query = query.Where(s => tagsetIds.Contains(s.TagsetId));
            }
            else if(tagValues is ["*"])
            {
                query = query.Where(s => EF.Functions.JsonExists(s.Tagset.Tags, tagKey));
            }
            else
            {
                query = query.Where(s => tagValues.Contains(s.Tagset.Tags.RootElement.GetProperty(tagKey).GetString()));
            }
        }

        var totalResults = await query.CountAsync(HttpContext.RequestAborted);

        if(request.Limit.HasValue && request.Limit.Value > 0) query = query.Take(request.Limit.Value);

        return new LookupResponseDto
        {
            Results = StreamResults(query),
            TotalResults = totalResults
        };
    }

    private async IAsyncEnumerable<LookupResponseDto.LookupResultDto> StreamResults(IQueryable<TimeSeries> query)
    {
        // To reduce wire traffic, return only the tagset ID, and look up the value using our cache

        var projected = query
            .Select(s => new
            {
                MetricName = s.Metric.Name,
                s.TagsetId
            });

        await foreach(var timeSeries in projected.AsAsyncEnumerable().WithCancellation(HttpContext.RequestAborted))
        {
            var tagset = tagsetCacheService.GetTagsetOrDefault(timeSeries.TagsetId);
            if(tagset is null) continue;
            yield return new LookupResponseDto.LookupResultDto
            {
                Metric = timeSeries.MetricName,
                Tags = tagset
            };
        }
    }
}
