using System.Text.Json;

namespace TimescaleOpenTsdbAdapter.Utils;

public static class Constants
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
