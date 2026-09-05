using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beamcast.Net;

public static class StreamStates
{
    public const string Live = "live";
    public const string Paused = "paused";
    public const string Ended = "ended";
}

/// <summary>Shared JSON settings for the small control messages.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static T? Deserialize<T>(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
