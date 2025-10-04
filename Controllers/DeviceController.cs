
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

public class DeviceController : Controller
{
    private readonly DeviceManager _deviceManager;
    private readonly DeviceWebSocketHandler _deviceWebSocketHandler;

    public DeviceController(DeviceManager deviceManager, DeviceWebSocketHandler deviceWebSocketHandler)
    {
        _deviceManager = deviceManager;
        _deviceWebSocketHandler = deviceWebSocketHandler;
    }

    // Dashboard view
    public IActionResult Index()
    {
        return View();
    }

    // Return all connected devices
    [HttpGet("Device/connected")]
    public IActionResult GetConnectedDevices()
    {
        var devices = _deviceManager.GetDevices()
            .Select(d => new { d.Id })
            .ToList();
        Console.WriteLine(devices);
        return Ok(devices);
    }

    // Request device status
    // Request device status
    [HttpGet("Device/localstatus/{deviceId}")]
    public async Task<IActionResult> GetLocalStatus(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            return NotFound("Device not connected.");
        if(device.Status == null)
            return NotFound("Device status not updated yet.");
        return Json(device.Status);
    }
    [HttpGet("Device/status/{deviceId}")]
    public async Task<IActionResult> GetStatus(string deviceId)
    {
       var device = _deviceManager.GetDevice(deviceId);
    if (device == null)
        return NotFound("Device not connected.");

    try
    {
        var status = await _deviceWebSocketHandler.RequestDeviceStatusAsync(deviceId);
            //Console.WriteLine(status.Wifi_Rssi);
                return Json(status); // <-- preferred
    }
    catch (Exception ex)
    {
        return StatusCode(500, $"Failed to get status: {ex.Message}");
    }
    }
        

    // Request file list
    [HttpGet("Device/files/{deviceId}")]
    public async Task<IActionResult> GetFiles(string deviceId)
    {
        try
        {
            var files = await _deviceWebSocketHandler.RequestFileListAsync(deviceId);
            var device = _deviceManager.GetDevice(deviceId);
            device.Files = files;
            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get files: {ex.Message}");
        }
        //var device = _deviceManager.GetDevice(deviceId);
        //if (device == null || device.Socket.State != WebSocketState.Open)
        //    return NotFound("Device not connected.");

        //var ws = device.Socket;
        //var tcs = new TaskCompletionSource<List<FileInfoModel>>();
        //var buffer = new byte[16 * 1024];

        //async Task HandleResponse(WebSocketReceiveResult result)
        //{
        //    if (result.MessageType != WebSocketMessageType.Text) return;

        //    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
        //    using var doc = JsonDocument.Parse(msg);

        //    if (doc.RootElement.ValueKind == JsonValueKind.Array)
        //    {
        //        var filesList = new List<FileInfoModel>();
        //        foreach (var f in doc.RootElement.EnumerateArray())
        //        {
        //            filesList.Add(new FileInfoModel
        //            {
        //                Name = f.GetProperty("name").GetString(),
        //                Size = f.GetProperty("size").GetInt32()
        //            });
        //        }
        //        tcs.TrySetResult(filesList);
        //    }
        //    else if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "ERROR")
        //    {
        //        var errMsg = doc.RootElement.GetProperty("message").GetString();
        //        tcs.TrySetException(new Exception(errMsg));
        //    }
        //}

        //var requestJson = "{\"type\":\"LIST_FILES\"}";
        //await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);

        //while (!tcs.Task.IsCompleted)
        //{
        //    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        //    await HandleResponse(result);
        //}

        //try
        //{
        //    var files = await tcs.Task;
        //    device.Files = files;
        //    return Ok(files);
        //}
        //catch (Exception ex)
        //{
        //    return StatusCode(500, $"Failed to get files: {ex.Message}");
        //}
    }

    // Trigger download 
    [HttpGet]
    public async Task<IActionResult> Download(string deviceId, string filename)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null) return NotFound("Device not connected.");

        var file = device.Files.FirstOrDefault(f => f.Name.Equals(filename));
        if (file == null) return NotFound("File not found.");

        try
        {
            var result = await _deviceWebSocketHandler.DownloadFileAsync(deviceId, file.Name, file.Id);
            Console.WriteLine("returned");
            return File(result.Data, "application/octet-stream", result.FileName);
        }
        catch (Exception ex)
        {
            //Console.WriteLine("Iam Here");
            Console.WriteLine(ex.Message);
            return StatusCode(500, $"Download failed: {ex.Message}");
        }
    }

    //[HttpPost]
    //public async Task<IActionResult> Download(string deviceId, string fileId)
    //{
    //    var device = _deviceManager.GetDevice(deviceId); if (device == null) return NotFound("Device not connected.");
    //    var file = device.Files.FirstOrDefault(f => f.Id == fileId);
    //    if (file == null) return NotFound("File not found.");
    //    var ws = device.Socket; var chunks = new ConcurrentDictionary<int, byte[]>();
    //    var tcs = new TaskCompletionSource<bool>();
    //    // Send REQUEST_FILE 
    //    var requestJson = $"{{\"type\":\"REQUEST_FILE\",\"filename\":\"{file.Name}\"}}";
    //    await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);
    //    try
    //    {
    //        while (!tcs.Task.IsCompleted)
    //        {
    //            var (msgType, data) = await _deviceWebSocketHandler.ReceiveFullMessage(ws, CancellationToken.None); var failedChunks = new HashSet<int>(); bool fileEnd = false; if (msgType == WebSocketMessageType.Text)
    //            {
    //                var msg = Encoding.UTF8.GetString(data); using var doc = JsonDocument.Parse(msg); var json = doc.RootElement; var type = json.GetProperty("type").GetString(); if (type == "FILE_START") { var ack = "{\"type\":\"FILE_START_ACK\"}"; await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, CancellationToken.None); Console.WriteLine($"📂 FILE_START for {json.GetProperty("filename").GetString()}"); }
    //                else if (type == "FILE_END")
    //                {
    //                    Console.WriteLine("📩 FILE_END received"); fileEnd = true;
    //                    // tcs.TrySetResult(true); 
    //                }
    //                else if (type == "ack") { Console.WriteLine("📩 FILE_END received"); tcs.TrySetResult(true); } else if (type == "ERROR") { tcs.TrySetException(new Exception("Device error: " + json.GetProperty("message").GetString())); }
    //            }
    //            else if (msgType == WebSocketMessageType.Binary)
    //            {
    //                if (data.Length == 0)
    //                {
    //                    if (fileEnd)
    //                    {
    //                        if (failedChunks.Count <= 0)
    //                        {
    //                            var complete = $"{{\"type\":\"COMPLETE\",\"fileId\":{fileId}}}";
    //                            await ws.SendAsync(Encoding.UTF8.GetBytes(complete), WebSocketMessageType.Text, true, CancellationToken.None);
    //                        }
    //                        else
    //                        {
    //                            string json = JsonSerializer.Serialize(failedChunks);
    //                            var retrySend = $"{{\"type\":\"RETRY\",\"chunks\":{json}}}";
    //                            await ws.SendAsync(Encoding.UTF8.GetBytes(retrySend), WebSocketMessageType.Text, true, CancellationToken.None);
    //                        }
    //                        // tcs.TrySetResult(true); 
    //                    }
    //                    else
    //                    {
    //                        // Console.WriteLine("📩 Empty binary frame (end marker)");
    //                        // tcs.TrySetResult(true);
    //                        int i = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
    //                        failedChunks.Add(i);
    //                    }
    //                }
    //                else
    //                {
    //                    int index = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
    //                    var chunk = new byte[data.Length - 4];
    //                    Array.Copy(data, 4, chunk, 0, chunk.Length); chunks[index] = chunk;
    //                    Console.WriteLine($"✅ Received chunk {index}, size={data.Length - 4} ");
    //                }
    //            }
    //            else if (msgType == WebSocketMessageType.Close)
    //            {
    //                tcs.TrySetException(new Exception("WebSocket closed during transfer"));
    //            }
    //        }
    //        await tcs.Task; // wait for success 
    //    }
    //    catch (Exception ex) { return StatusCode(500, $"Download failed: {ex.Message}"); }
    //    // Reassemble file 
    //    var fileData = chunks.OrderBy(k => k.Key).SelectMany(k => k.Value).ToArray();
    //    Console.WriteLine($"🎉 File assembled: {fileData.Length} bytes");
    //    return File(fileData, "application/octet-stream", file.Name);
    //}

    [HttpPost]
    public async Task<IActionResult> DeleteAllFiles(string deviceId)
    {
        try
        {
            var response = await _deviceWebSocketHandler.DeleteAllFilesAsync(deviceId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to delete files: {ex.Message}");
        }
        //var device = _deviceManager.GetDevice(deviceId);
        //if (device == null || device.Socket.State != WebSocketState.Open) return NotFound("Device not connected.");
        //var ws = device.Socket; var tcs = new TaskCompletionSource<IActionResult>();
        //async Task HandleResponse(WebSocketReceiveResult result, byte[] buffer)
        //{
        //    if (result.MessageType != WebSocketMessageType.Text) return;
        //    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
        //    using var doc = JsonDocument.Parse(msg);
        //    if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
        //    var type = typeProp.GetString() ?? "";
        //    doc.RootElement.TryGetProperty("type", out var messageProp);
        //    var message = messageProp.GetString() ?? ""; if (type == "success")
        //    {
        //        device.Files = new List<FileInfoModel>();
        //        Console.WriteLine(message);
        //    }
        //    else if (type == "ERROR")
        //    {
        //        var errMsg = doc.RootElement.GetProperty("message").GetString();
        //        tcs.TrySetException(new Exception(errMsg));
        //    }
        //}
        //var requestJson = "{\"type\":\"DELETE_ALL_FILES\"}";
        //await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None); var buffer = new byte[16 * 1024];
        //while (!tcs.Task.IsCompleted)
        //{
        //    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        //    await HandleResponse(result, buffer);
        //}
        //try
        //{
        //    return await tcs.Task;
        //    // will throw if ERROR or Close was triggered 
        //}
        //catch (Exception ex)
        //{
        //    Console.WriteLine($"Error: {ex.Message}"); return StatusCode(500, $"Error: {ex.Message}");
        //}
    }

    [HttpPost]
    public async Task<IActionResult> Disconnect(string deviceId)
    {
        try
        {
            var response = await _deviceWebSocketHandler.DisconnectDeviceAsync(deviceId);
            if(response.Success)
            {
                _deviceManager.RemoveDevice(deviceId);
                return Ok(new { success = response.Success, message = response.Message });

            }
            else
            {
                throw new Exception(response.Message);
            }

        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
        //var device = _deviceManager.GetDevice(deviceId);
        //if (device == null || device.Socket.State != WebSocketState.Open) return NotFound("Device not connected.");
        //var ws = device.Socket;
        //var tcs = new TaskCompletionSource<IActionResult>();
        //async Task HandleResponse(WebSocketReceiveResult result, byte[] buffer)
        //{
        //    if (result.MessageType != WebSocketMessageType.Text) return;
        //    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
        //    using var doc = JsonDocument.Parse(msg);
        //    if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
        //    var type = typeProp.GetString() ?? "";
        //    doc.RootElement.TryGetProperty("type", out var messageProp);
        //    var message = messageProp.GetString() ?? "";
        //    if (type == "success")
        //    {
        //        _deviceManager.RemoveDevice(deviceId);
        //    }
        //    else if (type == "ERROR")
        //    {
        //        var errMsg = doc.RootElement.GetProperty("message").GetString();
        //        tcs.TrySetException(new Exception(errMsg));
        //    }
        //}
        //var requestJson = "{\"type\":\"DISCONNECT\"}";
        //await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);
        //var buffer = new byte[16 * 1024]; while (!tcs.Task.IsCompleted)
        //{
        //    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        //    await HandleResponse(result, buffer);
        //}
        //try
        //{
        //    return await tcs.Task;
        //    // will throw if ERROR or Close was triggered 
        //}
        //catch (Exception ex)
        //{
        //    Console.WriteLine($"Error: {ex.Message}");
        //    return StatusCode(500, $"Error: {ex.Message}");
        //}
    }
    [HttpPost]
    public async Task<IActionResult> DeleteFile(string deviceId, string fileName)
    {
        try
        {
            var response = await _deviceWebSocketHandler.DeleteFileAsync(deviceId, fileName);
            return Ok(new { success = response.Success, message = response.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
        //var device = _deviceManager.GetDevice(deviceId);
        //if (device == null || device.Socket.State != WebSocketState.Open) return NotFound("Device not connected.");
        //var ws = device.Socket;
        //var tcs = new TaskCompletionSource<IActionResult>();
        //var file = device.Files.FirstOrDefault(a => a.Id.Equals(fileId));
        //async Task HandleResponse(WebSocketReceiveResult result, byte[] buffer)
        //{
        //    if (result.MessageType != WebSocketMessageType.Text) return;
        //    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
        //    using var doc = JsonDocument.Parse(msg);
        //    if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
        //    var type = typeProp.GetString() ?? "";
        //    doc.RootElement.TryGetProperty("type", out var messageProp);
        //    var message = messageProp.GetString() ?? "";
        //    if (type == "success")
        //    { device.Files.Remove(file); Console.WriteLine(message); }
        //    else if (type == "ERROR")
        //    {
        //        var errMsg = doc.RootElement.GetProperty("message").GetString();
        //        tcs.TrySetException(new Exception(errMsg));
        //    }
        //}
        //var requestJson = $"{{\"type\":\"DELETE_FILE\",\"filename\":\"{file.Name}\"}}";
        //await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);
        //var buffer = new byte[16 * 1024]; while (!tcs.Task.IsCompleted)
        //{
        //    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        //    await HandleResponse(result, buffer);
        //}
        //try
        //{
        //    return await tcs.Task;
        //    // will throw if ERROR or Close was triggered 
        //}
        //catch (Exception ex)
        //{
        //    Console.WriteLine($"Error: {ex.Message}");
        //    return StatusCode(500, $"Error: {ex.Message}");
        //}
    }

    // Show files of one device 
    public IActionResult Files(string deviceId, int page = 1, int pageSize = 10)
    {
        var device = _deviceManager.GetDevice(deviceId); if (device == null) return NotFound();
        var files = device.Files.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        ViewBag.TotalPages = (int)Math.Ceiling((double)device.Files.Count / pageSize);
        ViewBag.CurrentPage = page; ViewBag.DeviceId = deviceId;
        return View(files);
    }
}



// using System.Collections.Concurrent;
// using System.Net.WebSockets;
// using System.Text;
// using System.Text.Json;
// using Microsoft.AspNetCore.Mvc;

// public class DeviceController : Controller
// {
//     private readonly DeviceManager _deviceManager;
//     private readonly DeviceWebSocketHandler _deviceWebSocketHandler;

//     public DeviceController(DeviceManager deviceManager, DeviceWebSocketHandler deviceWebSocketHandler)
//     {
//         _deviceManager = deviceManager;
//         _deviceWebSocketHandler = deviceWebSocketHandler;
//     }

//     // Dashboard view
//     public IActionResult Index()
//     {
//         return View();
//     }

//     // Return all connected devices
//     [HttpGet("Device/connected")]
//     public IActionResult GetConnectedDevices()
//     {
//         var devices = _deviceManager.GetDevices()
//             .Select(d => new { d.Id })
//             .ToList();
//         Console.WriteLine(devices);
//         return Ok(devices);
//     }

//     // Request device status
//     [HttpGet("Device/status/{deviceId}")]
//     public async Task<IActionResult> GetStatus(string deviceId)
//     {
//         var device = _deviceManager.GetDevice(deviceId);
//         if (device == null || device.Socket.State != WebSocketState.Open)
//             return NotFound("Device not connected.");

//         try
//         {
//             var status = await _deviceWebSocketHandler.RequestDeviceStatusAsync(deviceId);
//             return Ok(status);
//         }
//         catch (Exception ex)
//         {
//             return StatusCode(500, $"Failed to get status: {ex.Message}");
//         }
//     }

//     // Request file list
//     [HttpGet("Device/files/{deviceId}")]
//     public async Task<IActionResult> GetFiles(string deviceId)
//     {
//         var device = _deviceManager.GetDevice(deviceId);
//         if (device == null || device.Socket.State != WebSocketState.Open)
//             return NotFound("Device not connected.");

//         var ws = device.Socket;
//         var tcs = new TaskCompletionSource<List<FileInfoModel>>();
//         var buffer = new byte[16 * 1024];

//         async Task HandleResponse(WebSocketReceiveResult result)
//         {
//             if (result.MessageType != WebSocketMessageType.Text) return;

//             var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
//             using var doc = JsonDocument.Parse(msg);

//             if (doc.RootElement.ValueKind == JsonValueKind.Array)
//             {
//                 var filesList = new List<FileInfoModel>();
//                 foreach (var f in doc.RootElement.EnumerateArray())
//                 {
//                     filesList.Add(new FileInfoModel
//                     {
//                         Name = f.GetProperty("name").GetString(),
//                         Size = f.GetProperty("size").GetInt32()
//                     });
//                 }
//                 tcs.TrySetResult(filesList);
//             }
//             else if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "ERROR")
//             {
//                 var errMsg = doc.RootElement.GetProperty("message").GetString();
//                 tcs.TrySetException(new Exception(errMsg));
//             }
//         }

//         var requestJson = "{\"type\":\"LIST_FILES\"}";
//         await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);

//         while (!tcs.Task.IsCompleted)
//         {
//             var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
//             await HandleResponse(result);
//         }

//         try
//         {
//             var files = await tcs.Task;
//             device.Files = files;
//             return Ok(files);
//         }
//         catch (Exception ex)
//         {
//             return StatusCode(500, $"Failed to get files: {ex.Message}");
//         }
//     }
// }
