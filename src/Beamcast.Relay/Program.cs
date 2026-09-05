using Beamcast.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var options = new RelayOptions
{
    AppKey = Environment.GetEnvironmentVariable("BEAMCAST_APP_KEY"),
};
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RelayHub>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/", () => Results.Text("beamcast relay", "text/plain"));
app.MapGet("/health", (RelayHub hub) => Results.Json(hub.Snapshot()));

app.Map("/ws", async (HttpContext context, RelayHub hub) =>
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

app.Logger.LogInformation("Beamcast relay up. App key {State}.", string.IsNullOrEmpty(options.AppKey) ? "NOT set (open relay)" : "set");
app.Run();
