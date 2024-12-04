using System.Collections.Frozen;
using System.Text.RegularExpressions;
using TimescaleOpenTsdbAdapter.Dtos;

namespace TimescaleOpenTsdbAdapter.TagFilters;

public static class TagFilterUtils
{
    public static ITagFilter CreateTagFilter(QueryFilterDto dto)
    {
        return dto.Type switch
        {
            FilterType.CaseInsensitiveLiteralOr => new CaseInsensitiveLiteralOrFilter(dto),
            FilterType.NotLiteralOr => new NotLiteralOrFilter(dto),
            FilterType.CaseInsensitiveNotLiteralOr => new CaseInsensitiveNotLiteralOrFilter(dto),
            FilterType.Wildcard => new WildcardFilter(dto),
            FilterType.CaseInsensitiveWildcard => new CaseInsensitiveWildcardFilter(dto),
            FilterType.RegularExpression => new RegularExpressionFilter(dto),
            _ => throw new ArgumentException($"Unhanded filter type {dto.Type}")
        };
    }
}

public interface ITagFilter
{
    public bool IsMatch(string tagv);
}

public class CaseInsensitiveLiteralOrFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly FrozenSet<string> values = dto.Filter.ToLowerInvariant().Split('|').ToFrozenSet();

    public bool IsMatch(string tagv) => values.Contains(tagv.ToLowerInvariant());
}

public class NotLiteralOrFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly FrozenSet<string> values = dto.Filter.Split('|').ToFrozenSet();

    public bool IsMatch(string tagv) => !values.Contains(tagv);
}

public class CaseInsensitiveNotLiteralOrFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly FrozenSet<string> values = dto.Filter.ToLowerInvariant().Split('|').ToFrozenSet();

    public bool IsMatch(string tagv) => !values.Contains(tagv.ToLowerInvariant());
}

public class WildcardFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly Regex regex = new (Regex.Escape(dto.Filter).Replace("\\*", ".*"), RegexOptions.Compiled);

    public bool IsMatch(string tagv) => regex.IsMatch(tagv);
}

public class CaseInsensitiveWildcardFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly Regex regex = new (Regex.Escape(dto.Filter).Replace("\\*", ".*"), RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool IsMatch(string tagv) => regex.IsMatch(tagv);
}

public class RegularExpressionFilter(QueryFilterDto dto) : ITagFilter
{
    private readonly Regex regex = new(dto.Filter, RegexOptions.Compiled);

    public bool IsMatch(string tagv) => regex.IsMatch(tagv);
}
