using System.Text.Json;

namespace BuildDiff;

/// <summary>
/// JSON helpers routed through the source-generated <see cref="AppJsonContext"/> so all
/// (de)serialization is Native-AOT-safe — no reflection, no runtime code generation.
/// Every payload type must be registered in <see cref="AppJsonContext"/>.
/// </summary>
public static class Json
{
    public static JsonElement ToElement(object value)
        => JsonSerializer.SerializeToElement(value, TypeInfo(value.GetType()));

    public static T? To<T>(JsonElement? el) where T : class
        => el is null ? null : (T?)JsonSerializer.Deserialize(el.Value, TypeInfo(typeof(T)));

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo TypeInfo(Type t)
        => AppJsonContext.Default.GetTypeInfo(t)
           ?? throw new InvalidOperationException($"Type {t} is not registered in AppJsonContext — add a [JsonSerializable] for it.");
}
