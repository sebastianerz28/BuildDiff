using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDiff;

/// <summary>Shared JSON settings so every payload round-trips identically.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? To<T>(JsonElement? el) where T : class
        => el is null ? null : el.Value.Deserialize<T>(Options);
}
