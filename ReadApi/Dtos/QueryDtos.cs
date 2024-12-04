using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimescaleOpenTsdbAdapter.Dtos;

public class QueryFilterDto
{
    [Required]
    public FilterType? Type { get; init; }
    public required string Tagk { get; init; }
    public required string Filter { get; init; }
    public bool GroupBy { get; init; }
}

[JsonConverter(typeof(FilterTypeConverter))]
public enum FilterType
{
    LiteralOr,
    CaseInsensitiveLiteralOr,
    NotLiteralOr,
    CaseInsensitiveNotLiteralOr,
    Wildcard,
    CaseInsensitiveWildcard,
    RegularExpression
}

public class FilterTypeConverter : JsonConverter<FilterType>
{
    public static FilterType Parse(string filterString)
    {
        return filterString switch
        {
            "literal_or" => FilterType.LiteralOr,
            "iliteral_or" => FilterType.CaseInsensitiveLiteralOr,
            "not_literal_or" => FilterType.NotLiteralOr,
            "not_iliteral_or" => FilterType.CaseInsensitiveNotLiteralOr,
            "wildcard" => FilterType.Wildcard,
            "iwildcard" => FilterType.CaseInsensitiveWildcard,
            "regexp" => FilterType.RegularExpression,
            _ => throw new ArgumentException($"Unrecognised filter type \"{filterString}\"", nameof(filterString))
        };
    }

    public override FilterType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Invalid token type {reader.TokenType}");
        try
        {
            return Parse(reader.GetString()!);
        }
        catch(ArgumentException e)
        {
            throw new JsonException("Failed to parse filter type", e);
        }
    }

    public override void Write(Utf8JsonWriter writer, FilterType value, JsonSerializerOptions options)
    {
        // Placeholder implementation, just enough to generate an example payload for the swagger UI
        writer.WriteStringValue("iliteral_or");
    }
}
