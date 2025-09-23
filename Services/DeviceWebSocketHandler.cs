
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
        //var device = new DeviceInfo { Id = deviceId };
        _deviceManager.AddDevice(deviceId, socket);

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
                    HandleMessage(deviceId, json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Handle file chunk upload (not covered here yet)
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
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var type)) return;

        if (type.GetString() == "FILE_LIST")
        {
            var files = new List<FileInfoModel>();
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                files.Add(new FileInfoModel
                {
                    Id = f.GetProperty("id").GetString() ?? Guid.NewGuid().ToString(),
                    Name = f.GetProperty("name").GetString() ?? "unknown",
                    Size = f.GetProperty("size").GetInt64()
                });
            }

            var device = _deviceManager.GetDevice(deviceId);
            if (device != null)
            {
                device.Files = files;
            }
        }
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
