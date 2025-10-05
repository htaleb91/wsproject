using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using WsProjet.Services;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DeviceWebSocketHandler>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Enable WebSockets
app.UseWebSockets();

// MVC routing
app.UseRouting();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Device}/{action=Index}/{id?}");

// Map SignalR hub
app.MapHub<DownloadHub>("/downloadHub");
// WebSocket endpoint
app.Map("/ws/{deviceId}", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    string deviceId = context.Request.RouteValues["deviceId"]?.ToString() ?? "unknown";
    var ws = await context.WebSockets.AcceptWebSocketAsync();

    var manager = context.RequestServices.GetRequiredService<DeviceManager>();
    var handler = context.RequestServices.GetRequiredService<DeviceWebSocketHandler>();

    // Add device to manager
    manager.AddDevice(deviceId, ws);
    var device = manager.GetDevice(deviceId);
   Console.WriteLine($"Device connected: {deviceId}");

    // Start background receive loop
    _ = Task.Run(() => handler.StartReceiveLoopAsync(device, CancellationToken.None));

    // Keep request alive until socket closes
    while (ws.State == WebSocketState.Open)
    {
        await Task.Delay(1000);
    }

    manager.RemoveDevice(deviceId);
    Console.WriteLine($"Device disconnected: {deviceId}");
});

app.Run();
