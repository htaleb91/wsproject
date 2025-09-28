// var builder = WebApplication.CreateBuilder(args);

// // Register connection manager as singleton
// builder.Services.AddSingleton<DeviceManager>();
// builder.Services.AddSingleton<DeviceWebSocketHandler>();
// builder.Services.AddControllersWithViews();
// var app = builder.Build();

// app.UseWebSockets();

// app.MapControllerRoute(
//     name: "default",
//     pattern: "{controller=Device}/{action=Index}");


// app.Map("/ws/{deviceId}", async context =>
// {
//     var manager = context.RequestServices.GetRequiredService<DeviceManager>();

//     if (context.WebSockets.IsWebSocketRequest)
//     {
//         string deviceId = context.Request.RouteValues["deviceId"]?.ToString() ?? "unknown";
//         var ws = await context.WebSockets.AcceptWebSocketAsync();

//         manager.AddDevice(deviceId, ws);
//         Console.WriteLine($"Device connected: {deviceId}");

//         var handler = new DeviceWebSocketHandler(manager);
//         await handler.HandleAsync(ws, deviceId); // pass (socket, deviceId)

//         Console.WriteLine($"Device disconnected: {deviceId}");
//     }
//     else
//     {
//         context.Response.StatusCode = 400;
//     }
// });

// app.Run();
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddSingleton<DeviceWebSocketHandler>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Middleware
app.UseWebSockets();
app.UseRouting();
app.UseAuthorization();

// Map WebSocket endpoint
app.Map("/ws/{deviceId}", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var deviceId = context.Request.RouteValues["deviceId"]?.ToString() ?? "unknown";
    using var ws = await context.WebSockets.AcceptWebSocketAsync();

    var manager = context.RequestServices.GetRequiredService<DeviceManager>();
    manager.AddDevice(deviceId, ws);
  
    var handler = context.RequestServices.GetRequiredService<DeviceWebSocketHandler>();
    var device = manager.GetDevice(deviceId);
    if (device != null)
    {
        if (!device.ReceiveLoopStarted)
        {
            device.ReceiveLoopStarted = true; // track so we don’t start multiple loops
                                              // ?? Start background receive loop
            _ = Task.Run(() => handler.StartReceiveLoopAsync(device, CancellationToken.None));
        }
        // _ = Task.Run(() => handler.StartReceiveLoopAsync(deviceId, CancellationToken.None));
    }
    await handler.HandleAsync(ws, deviceId);

    Console.WriteLine($"Device connected: {deviceId}");


    // Console.WriteLine($"Device disconnected: {deviceId}");
});


// Optional: endpoint to force disconnect (from HTML)
// app.Map("/ws/disconnect/{deviceId}", async context =>
// {
//     if (!context.WebSockets.IsWebSocketRequest)
//     {
//         context.Response.StatusCode = 400;
//         return;
//     }
//     var deviceId = context.Request.RouteValues["deviceId"]?.ToString();
//     var manager = context.RequestServices.GetRequiredService<DeviceManager>();
//     var device = manager.GetDevice(deviceId!);

//     if (device != null)
//     {
//         await device.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected by server", CancellationToken.None);
//         manager.RemoveDevice(deviceId!);
//         await context.Response.WriteAsync($"Device {deviceId} disconnected.");
//         Console.WriteLine($"Device {deviceId} disconnected via API.");
//     }
//     else
//     {
//         context.Response.StatusCode = 404;
//         await context.Response.WriteAsync("Device not found");
//     }
// });
// MVC route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Device}/{action=Index}/{id?}");

app.Run();












// // var builder = WebApplication.CreateBuilder(args);

// // // Add services to the container.
// // builder.Services.AddControllersWithViews();

// // var app = builder.Build();

// // // Configure the HTTP request pipeline.
// // if (!app.Environment.IsDevelopment())
// // {
// //     app.UseExceptionHandler("/Home/Error");
// //     // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
// //     app.UseHsts();
// // }

// // app.UseHttpsRedirection();
// // app.UseRouting();

// // app.UseAuthorization();

// // app.MapStaticAssets();

// // app.MapControllerRoute(
// //     name: "default",
// //     pattern: "{controller=Home}/{action=Index}/{id?}")
// //     .WithStaticAssets();


// // app.Run();
