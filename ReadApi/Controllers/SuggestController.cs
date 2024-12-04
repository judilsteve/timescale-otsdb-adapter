using TimescaleOpenTsdbAdapter.Services;
using Microsoft.AspNetCore.Mvc;

namespace TimescaleOpenTsdbAdapter.Controllers;

[ApiController]
[Route("api/suggest")]
public class SuggestController(
    TagsetCacheService tagsetCacheService
) : ControllerBase
{
    public enum ParameterType
    {
        metrics,
        tagv,
        tagk
    }

    private readonly TagsetCacheService tagsetCacheService = tagsetCacheService;

    // See http://opentsdb.net/docs/build/html/api_http/suggest.html
    [HttpGet]
    public IEnumerable<string> Suggest(ParameterType type, string? q, int max = 25)
    {
        var suggestions = type switch
        {
            ParameterType.metrics => tagsetCacheService.GetMetricNames(),
            // Tries would make sense here instead of naive search, but the source needs to be thread-safe
            ParameterType.tagk => tagsetCacheService.GetTagKeys(),
            ParameterType.tagv => tagsetCacheService.GetTagValues(),
            _ => throw new ArgumentException($"Unhandled parameter type {type}", nameof(type)),
        };

        if(!string.IsNullOrEmpty(q))
        {
            suggestions = suggestions.Where(v => v.StartsWith(q));
        }

        return suggestions.Take(max);
    }

    [HttpGet("tagValues/{tagKey}")]
    public IEnumerable<string> SuggestTagValues(string tagKey, string? q, int max = 25)
    {
        var tagValues = tagsetCacheService.GetTagValues(tagKey);

        if (!string.IsNullOrEmpty(q)) tagValues = tagValues.Where(v => v.StartsWith(q));

        return tagValues.Take(max);
    }

    [HttpGet("tagKeys/{metric}")]
    public IEnumerable<string> SuggestTagKeys(string metric, string? q, int max = 25)
    {
        var tagKeys = tagsetCacheService.GetTagKeys(metric);

        if (!string.IsNullOrEmpty(q)) tagKeys = tagKeys.Where(v => v.StartsWith(q));

        return tagKeys.Take(max);
    }
}
