using System.Collections.Concurrent;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

public class DeviceController : Controller
{
    private readonly DeviceManager _deviceManager;

    public DeviceController(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    // Show all connected devices
    public IActionResult Index()
    {
        var devices = _deviceManager.GetDevices();
        return View(devices);
    }

    // Show files of one device
    public IActionResult Files(string deviceId, int page = 1, int pageSize = 10)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null) return NotFound();

        var files = device.Files.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        ViewBag.TotalPages = (int)Math.Ceiling((double)device.Files.Count / pageSize);
        ViewBag.CurrentPage = page;
        ViewBag.DeviceId = deviceId;

        return View(files);
    }

    // Trigger download
    [HttpPost]
    public async Task<IActionResult> Download(string deviceId, string fileId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null) return NotFound("Device not connected.");

        var file = device.Files.FirstOrDefault(f => f.Id == fileId);
        if (file == null) return NotFound("File not found.");

        var ws = device.Socket;

        var chunks = new ConcurrentDictionary<int, byte[]>();
        var tcs = new TaskCompletionSource<bool>();

        // Send REQUEST_FILE
        var requestJson = $"{{\"type\":\"REQUEST_FILE\",\"filename\":\"{file.Name}\"}}";
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(requestJson),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

        try
        {
            while (!tcs.Task.IsCompleted)
            {
                var (msgType, data) = await ReceiveFullMessage(ws, CancellationToken.None);

                if (msgType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(data);
                    using var doc = JsonDocument.Parse(msg);
                    var json = doc.RootElement;
                    var type = json.GetProperty("type").GetString();

                    if (type == "FILE_START")
                    {
                        var ack = "{\"type\":\"FILE_START_ACK\"}";
                        await ws.SendAsync(
                            Encoding.UTF8.GetBytes(ack),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                        Console.WriteLine($"📂 FILE_START for {json.GetProperty("filename").GetString()}");
                    }
                    else if (type == "FILE_END")
                    {
                        Console.WriteLine("📩 FILE_END received");
                        tcs.TrySetResult(true);
                    }
                    else if (type == "ERROR")
                    {
                        tcs.TrySetException(new Exception("Device error: " + json.GetProperty("message").GetString()));
                    }
                }
                else if (msgType == WebSocketMessageType.Binary)
                {

                    // if (data.Length == 0)
                    // {
                    //     Console.WriteLine("📩 Empty binary frame (end marker)");
                    //     tcs.TrySetResult(true);
                    // }
                    // else
                    // {
                    int index = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    var chunk = new byte[data.Length - 4];
                    Array.Copy(data, 4, chunk, 0, chunk.Length);
                    chunks[index] = chunk;

                    Console.WriteLine($"✅ Received chunk {index}, size={data.Length - 4} ");

                    // }
                }
                else if (msgType == WebSocketMessageType.Close)
                {
                    tcs.TrySetException(new Exception("WebSocket closed during transfer"));
                }
            }

            await tcs.Task; // wait for success
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Download failed: {ex.Message}");
        }

        // Reassemble file
        var fileData = chunks.OrderBy(k => k.Key).SelectMany(k => k.Value).ToArray();
        Console.WriteLine($"🎉 File assembled: {fileData.Length} bytes");

        return File(fileData, "application/octet-stream", file.Name);
    }

    // public async Task<IActionResult> Download(string deviceId, string fileId)
    // {
    //     var device = _deviceManager.GetDevice(deviceId);
    //     if (device == null) return NotFound("Device not connected.");

    //     var fileName = device.Files.FirstOrDefault(a => a.Id == fileId)?.Name;
    //     if (fileName == null) return NotFound("File not found on device.");

    //     var ws = device.Socket;
    //     var chunks = new ConcurrentDictionary<int, byte[]>();
    //     var tcs = new TaskCompletionSource<bool>();

    //     // 🔹 Send REQUEST_FILE
    //     var requestJson = $"{{\"type\":\"REQUEST_FILE\",\"filename\":\"{fileName}\"}}";
    //     await ws.SendAsync(
    //         Encoding.UTF8.GetBytes(requestJson),
    //         WebSocketMessageType.Text,
    //         true,
    //         CancellationToken.None
    //     );

    //     var buffer = new byte[16 * 1024];

    //     while (!tcs.Task.IsCompleted)
    //     {
    //         using var ms = new MemoryStream();
    //         WebSocketReceiveResult result;

    //         // 🔹 Keep receiving until EndOfMessage = true
    //         do
    //         {
    //             result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    //             ms.Write(buffer, 0, result.Count);
    //         } while (!result.EndOfMessage);

    //         byte[] messageBytes = ms.ToArray();

    //         if (result.MessageType == WebSocketMessageType.Text)
    //         {
    //             var msg = Encoding.UTF8.GetString(messageBytes);
    //             using var doc = JsonDocument.Parse(msg);
    //             var json = doc.RootElement;

    //             var type = json.GetProperty("type").GetString();
    //             if (type == "FILE_START")
    //             {
    //                 var size = json.GetProperty("size").GetInt32();
    //                 Console.WriteLine($"File transfer starting: {fileName} ({size} bytes)");

    //                 // Acknowledge to device
    //                 var ack = "{\"type\":\"FILE_START_ACK\"}";
    //                 await ws.SendAsync(
    //                     Encoding.UTF8.GetBytes(ack),
    //                     WebSocketMessageType.Text,
    //                     true,
    //                     CancellationToken.None
    //                 );
    //             }
    //             else if (type == "FILE_END")
    //             {
    //                 Console.WriteLine("File transfer ended (FILE_END).");
    //                 tcs.TrySetResult(true);
    //             }
    //             else if (type == "ERROR")
    //             {
    //                 var msgErr = json.GetProperty("message").GetString();
    //                 tcs.TrySetException(new Exception($"Device error: {msgErr}"));
    //             }
    //         }
    //         else if (result.MessageType == WebSocketMessageType.Binary)
    //         {
    //             if (messageBytes.Length <= 4)
    //             {
    //                 Console.WriteLine("[WARN] Received binary frame with no data.");
    //                 continue;
    //             }

    //             // First 4 bytes = chunk index
    //             int index = (messageBytes[0] << 24) | (messageBytes[1] << 16) |
    //                         (messageBytes[2] << 8) | messageBytes[3];

    //             byte[] data = new byte[messageBytes.Length - 4];
    //             Array.Copy(messageBytes, 4, data, 0, data.Length);

    //             chunks[index] = data;
    //             Console.WriteLine($"✅ Received chunk {index} (size {data.Length})");
    //         }
    //         else if (result.MessageType == WebSocketMessageType.Close)
    //         {
    //             tcs.TrySetException(new Exception("WebSocket closed during transfer"));
    //         }
    //     }

    //     try
    //     {
    //         await tcs.Task; // wait for FILE_END or error
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"Download failed: {ex.Message}");
    //         return StatusCode(500, $"Download failed: {ex.Message}");
    //     }

    //     // 🔹 Reassemble file
    //     var ordered = chunks.OrderBy(k => k.Key).Select(k => k.Value).ToList();
    //     var fileData = ordered.SelectMany(b => b).ToArray();

    //     Console.WriteLine($"File assembled: {fileData.Length} bytes");

    //     return File(fileData, "application/octet-stream", fileName);
    // }

    private async Task<(WebSocketMessageType type, byte[] data)> ReceiveFullMessage(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[32 * 1024]; // bigger buffer for large chunks

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            Console.WriteLine(result.Count);
            if (result.Count > 0)
                ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, ms.ToArray());
    }


}
