using Beamcast.Net;
using Beamcast.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var ttlEnv = Environment.GetEnvironmentVariable("BEAMCAST_LOUNGE_TTL_HOURS");
var options = new ServerOptions
{
    AppKey = Environment.GetEnvironmentVariable("BEAMCAST_APP_KEY"),
    HostName = Environment.GetEnvironmentVariable("BEAMCAST_HOST_NAME") is { Length: > 0 } hostName ? hostName : Environment.MachineName,
    DataDirectory = Environment.GetEnvironmentVariable("BEAMCAST_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"),
    DefaultTemporaryTtlHours = double.TryParse(ttlEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0
        ? LoungeProtocol.ClampTtlHours(hours)
        : LoungeProtocol.DefaultTtlHours,
};
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LoungeStore>();
builder.Services.AddSingleton<LoungeHub>();
builder.Services.AddHostedService<LoungeJanitor>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => Results.Text("beamcast server v" + typeof(LoungeHub).Assembly.GetName().Version, "text/plain"));
app.MapGet("/health", (LoungeHub hub) => Results.Json(hub.Snapshot()));

app.MapGet(LoungeProtocol.InfoPath, (HttpContext context, LoungeHub hub) =>
    hub.KeyMatches(context.Request.Headers[LoungeProtocol.AppKeyHeader].FirstOrDefault())
        ? Results.Json(hub.HostInfo(includeRooms: false))
        : Results.Json(new { reason = LoungeProtocol.ReasonBadKey }, statusCode: StatusCodes.Status403Forbidden));

app.MapGet(LoungeProtocol.RoomsPath, (HttpContext context, LoungeHub hub) =>
    hub.KeyMatches(context.Request.Headers[LoungeProtocol.AppKeyHeader].FirstOrDefault())
        ? Results.Json(hub.HostInfo(includeRooms: true))
        : Results.Json(new { reason = LoungeProtocol.ReasonBadKey }, statusCode: StatusCodes.Status403Forbidden));

app.Map(LoungeProtocol.DefaultPath, async (HttpContext context, LoungeHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket only.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.HandleAsync(socket, ClientAddress(context), context.RequestAborted);
});

app.Services.GetRequiredService<LoungeHub>().LoadPersisted();
app.Logger.LogInformation(
    "Beamcast server \"{Host}\" up. App key {Key}; data in {Data}; temporary rooms live {Ttl} h when empty.",
    options.HostName,
    string.IsNullOrEmpty(options.AppKey) ? "not set (anyone with the address can create rooms)" : "set",
    options.DataDirectory,
    options.DefaultTemporaryTtlHours
);
app.Run();

// Behind Cloudflare or a reverse proxy every socket arrives from the proxy; the forwarded header
// is the only per-client handle for rate limiting. Trusting it only lets a client dodge its own
// limit, never someone else's.
static string ClientAddress(HttpContext context)
{
    var forwarded = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
    return string.IsNullOrWhiteSpace(forwarded) ? context.Connection.RemoteIpAddress?.ToString() ?? "?" : forwarded;
}
