var builder = WebApplication.CreateBuilder(args);

// Register connection manager as singleton
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddControllersWithViews();
var app = builder.Build();

app.UseWebSockets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Device}/{action=Index}");
app.Map("/ws/{deviceId}", async context =>
{
    var manager = context.RequestServices.GetRequiredService<DeviceManager>();

    if (context.WebSockets.IsWebSocketRequest)
    {
        string deviceId = context.Request.RouteValues["deviceId"]?.ToString() ?? "unknown";
        var ws = await context.WebSockets.AcceptWebSocketAsync();

        manager.AddDevice(deviceId, ws);
        Console.WriteLine($"Device connected: {deviceId}");

        var handler = new DeviceWebSocketHandler(manager);
        await handler.HandleAsync(ws, deviceId); // pass (socket, deviceId)

        Console.WriteLine($"Device disconnected: {deviceId}");
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();













// var builder = WebApplication.CreateBuilder(args);

// // Add services to the container.
// builder.Services.AddControllersWithViews();

// var app = builder.Build();

// // Configure the HTTP request pipeline.
// if (!app.Environment.IsDevelopment())
// {
//     app.UseExceptionHandler("/Home/Error");
//     // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
//     app.UseHsts();
// }

// app.UseHttpsRedirection();
// app.UseRouting();

// app.UseAuthorization();

// app.MapStaticAssets();

// app.MapControllerRoute(
//     name: "default",
//     pattern: "{controller=Home}/{action=Index}/{id?}")
//     .WithStaticAssets();


// app.Run();
