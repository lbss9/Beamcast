using Beamcast.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var options = new ServerOptions
{
    AppKey = Environment.GetEnvironmentVariable("BEAMCAST_APP_KEY"),
    DataDirectory = Environment.GetEnvironmentVariable("BEAMCAST_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"),
    EmptyLoungeTtl = TimeSpan.FromHours(double.TryParse(Environment.GetEnvironmentVariable("BEAMCAST_LOUNGE_TTL_HOURS"), out var hours) ? hours : 0),
};
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LoungeStore>();
builder.Services.AddSingleton<LoungeHub>();
builder.Services.AddHostedService<LoungeJanitor>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => Results.Text("beamcast server v" + typeof(LoungeHub).Assembly.GetName().Version, "text/plain"));
app.MapGet("/health", (LoungeHub hub) => Results.Json(hub.Snapshot()));

app.Map("/ws", async (HttpContext context, LoungeHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket only.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var remote = context.Connection.RemoteIpAddress?.ToString() ?? "?";
    await hub.HandleAsync(socket, remote, context.RequestAborted);
});

app.Services.GetRequiredService<LoungeHub>().LoadPersisted();
app.Logger.LogInformation(
    "Beamcast server up. App key {Key}; data in {Data}; empty-lounge TTL {Ttl}.",
    string.IsNullOrEmpty(options.AppKey) ? "not set (anyone with the address can create lounges)" : "set",
    options.DataDirectory,
    options.EmptyLoungeTtl == TimeSpan.Zero ? "forever" : options.EmptyLoungeTtl.ToString()
);
app.Run();
