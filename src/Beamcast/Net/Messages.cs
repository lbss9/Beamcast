using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beamcast.Net;

/// <summary>Host → viewer, first message after the TCP connect. Carries the auth nonce.</summary>
public sealed class ChallengeMessage
{
    public int Protocol { get; set; } = 1;
    public string Nonce { get; set; } = string.Empty;
    public bool RequiresPassword { get; set; }
}

/// <summary>Viewer → host, answers the challenge.</summary>
public sealed class HelloMessage
{
    public int Protocol { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string? Auth { get; set; }
    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>Host → viewer once the handshake is accepted.</summary>
public sealed class WelcomeMessage
{
    public string SessionName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Codec { get; set; } = "vp8";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string State { get; set; } = StreamStates.Live;
    public List<string> Viewers { get; set; } = [];
}

public sealed class RejectMessage
{
    public string Reason { get; set; } = RejectReasons.Unknown;
}

public static class RejectReasons
{
    public const string Password = "password";
    public const string Full = "full";
    public const string Version = "version";
    public const string Unknown = "unknown";
}

public sealed class ViewersMessage
{
    public List<string> Viewers { get; set; } = [];
}

public sealed class StreamStateMessage
{
    public string State { get; set; } = StreamStates.Live;
}

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
}
