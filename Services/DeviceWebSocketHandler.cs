
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class DeviceWebSocketHandler
{
    private readonly DeviceManager _deviceManager;

    public DeviceWebSocketHandler(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    public async Task HandleAsync(WebSocket socket, string deviceId)
    {
        var buffer = new byte[16 * 1024]; // 16 KB chunks

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _deviceManager.RemoveDevice(deviceId);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                    break;
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // 🔹 Handle both auto-status and response-to-request
                    HandleMessage(deviceId, json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Handle file chunks (not shown here)
                }
            }
        }
        finally
        {
            _deviceManager.RemoveDevice(deviceId);
        }
    }

    private void HandleMessage(string deviceId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";

                if (type == "STATUS")
                {
                    // 🔹 Parse into DeviceStatusInfo
                    var status = new DeviceStatusInfo
                    {
                        Uptime = root.GetProperty("uptime").GetString() ?? "0",
                        Heap = root.GetProperty("heap").GetString() ?? "0",
                        Wifi_Rssi = root.GetProperty("wifi_rssi").GetString() ?? "0",
                        Wifi_Ip = root.GetProperty("wifi_ip").GetString() ?? "0",
                        Connected = root.GetProperty("connected").GetBoolean()
                    };

                    // 🔹 Save status in DeviceManager
                    _deviceManager.UpdateDeviceStatus(deviceId, status);

                    // Console.WriteLine($"[STATUS] Device {deviceId}: IP={status.Wifi_Ip}, RSSI={status.Wifi_Rssi}");
                }
                else
                {
                    Console.WriteLine($"[INFO] Device {deviceId} sent: {json}");
                }
            }
            else
            {
                Console.WriteLine($"[WARN] Unknown JSON from device {deviceId}: {json}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to parse device message: {ex.Message}");
        }
    }


    public async Task<DeviceStatusInfo> RequestDeviceStatusAsync(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null || device.Socket.State != WebSocketState.Open)
            throw new Exception("Device not connected.");

        var ws = device.Socket;
        var tcs = new TaskCompletionSource<DeviceStatusInfo>();

        async Task HandleResponse(WebSocketReceiveResult result, byte[] buffer)
        {
            if (result.MessageType != WebSocketMessageType.Text) return;

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var doc = JsonDocument.Parse(msg);

            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString() ?? "";

            if (type == "STATUS")
            {
                var status = new DeviceStatusInfo
                {
                    Uptime = doc.RootElement.TryGetProperty("uptime", out var uptime) ? uptime.GetRawText() : null,
                    Heap = doc.RootElement.TryGetProperty("heap", out var heap) ? heap.GetRawText() : null,
                    Wifi_Rssi = doc.RootElement.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetRawText() : null,
                    Wifi_Ip = doc.RootElement.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
                    Connected = doc.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean()
                };

                // ✅ Update the property in DeviceInfo
                device.Status = status;

                tcs.TrySetResult(status);
            }
            else if (type == "ERROR")
            {
                var errMsg = doc.RootElement.GetProperty("message").GetString();
                tcs.TrySetException(new Exception(errMsg));
            }
        }

        // Send STATUS request
        var requestJson = "{\"type\":\"STATUS\"}";
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(requestJson),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

        var buffer = new byte[16 * 1024];
        while (!tcs.Task.IsCompleted)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            await HandleResponse(result, buffer);
        }

        return await tcs.Task;
    }


    public async Task<(WebSocketMessageType type, byte[] data)> ReceiveFullMessage(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[32 * 1024]; // bigger buffer for large chunks

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            // Console.WriteLine(result.Count);
            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, ms.ToArray());
    }

    
}












// using System.Net.WebSockets;
// using System.Text;
// using System.Text.Json;
// using System.Collections.Concurrent;

// public class DeviceWebSocketHandler
// {
//     private readonly DeviceConnectionManager _manager;

//     public DeviceWebSocketHandler(DeviceConnectionManager manager)
//     {
//         _manager = manager;
//     }

//     private class UploadSession
//     {
//         public string Filename { get; set; } = "";
//         public long Size { get; set; }
//         public ConcurrentDictionary<int, byte[]> Chunks { get; } = new();
//     }

//     private readonly ConcurrentDictionary<string, UploadSession> _uploads = new();

//     public async Task HandleAsync(string deviceId, WebSocket ws)
//     {
//         var buffer = new byte[16 * 1024];

//         while (ws.State == WebSocketState.Open)
//         {
//             var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

//             if (result.MessageType == WebSocketMessageType.Close)
//             {
//                 await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
//                 break;
//             }

//             if (result.MessageType == WebSocketMessageType.Text)
//             {
//                 string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
//                 await HandleTextMessage(deviceId, ws, msg);
//             }
//             else if (result.MessageType == WebSocketMessageType.Binary)
//             {
//                 await HandleBinaryMessage(deviceId, ws, buffer, result.Count);
//             }
//         }
//     }

//     private async Task HandleTextMessage(string deviceId, WebSocket ws, string json)
//     {
//         try
//         {
//             using var doc = JsonDocument.Parse(json);
//             string type = doc.RootElement.GetProperty("type").GetString() ?? "";

//             switch (type)
//             {
//                 case "STATUS":
//                     Console.WriteLine($"[{deviceId}] STATUS: {json}");
//                     break;

//                 case "FILE_START":
//                     var filename = doc.RootElement.GetProperty("filename").GetString()!;
//                     var size = doc.RootElement.GetProperty("size").GetInt64();

//                     var session = new UploadSession { Filename = filename, Size = size };
//                     _uploads[deviceId] = session;

//                     Console.WriteLine($"[{deviceId}] Upload start {filename}, {size} bytes");
//                     await SendJson(ws, new { type = "FILE_START_ACK" });
//                     break;

//                 case "FILE_END":
//                     Console.WriteLine($"[{deviceId}] File transfer complete marker.");
//                     break;

//                 default:
//                     Console.WriteLine($"[{deviceId}] Unhandled message: {json}");
//                     break;
//             }
//         }
//         catch (Exception ex)
//         {
//             Console.WriteLine($"[{deviceId}] Bad JSON: {ex.Message}");
//         }
//     }

//     private async Task HandleBinaryMessage(string deviceId, WebSocket ws, byte[] data, int count)
//     {
//         if (!_uploads.TryGetValue(deviceId, out var session))
//         {
//             if (count == 0)
//             {
//                 Console.WriteLine($"[{deviceId}] Empty binary frame (completion marker).");
//             }
//             return;
//         }

//         if (count == 0)
//         {
//             Console.WriteLine($"[{deviceId}] Received final empty binary frame.");
//             await SendJson(ws, new { type = "COMPLETE", fileId = 42 });
//             return;
//         }

//         int chunkIndex = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
//         var payload = data.Skip(4).Take(count - 4).ToArray();

//         session.Chunks[chunkIndex] = payload;
//         Console.WriteLine($"[{deviceId}] Received chunk {chunkIndex}, {payload.Length} bytes");
//     }

//     private static async Task SendJson(WebSocket ws, object obj)
//     {
//         string json = JsonSerializer.Serialize(obj);
//         var bytes = Encoding.UTF8.GetBytes(json);
//         await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
//     }
// }
