
using System.Net.Sockets;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

public class DeviceWebSocketHandler
{
    private readonly DeviceManager _deviceManager;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pendingRequests
    = new();

    public DeviceWebSocketHandler(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }
    #region Handler Generic Helper


    private async Task<T> SendRequestAsync<T>(
        string deviceId,
        string requestType,
        string requestJson,
        Func<JsonDocument, T> parseResponse)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null || device.Socket.State != WebSocketState.Open)
            throw new Exception("Device not connected.");

        var ws = device.Socket;
        var key = $"{deviceId}:{requestType}";

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[key] = tcs;

        // Ensure buffer and loop are ready before sending
        await Task.Delay(10); // small delay ensures loop is ready (optional)
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(requestJson),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

        var resultObj = await tcs.Task;
        return (T)resultObj;
    }


    #endregion


    #region Recieve Loop
    public async Task StartReceiveLoopAsync(DeviceInfo device, CancellationToken ct)
    {
        
        var ws = device.Socket;
        var buffer = new byte[16 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Socket closed.");
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var msg = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"Received from {device.Id}: {msg}"); // Debug log

                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    // File list response
                    var files = new List<FileInfoModel>();
                    foreach (var f in root.EnumerateArray())
                    {
                        files.Add(new FileInfoModel
                        {
                            Name = f.GetProperty("name").GetString() ?? "",
                            Size = f.GetProperty("size").GetInt32()
                        });
                    }

                    var key = $"{device.Id}:LIST_FILES";
                    if (_pendingRequests.TryRemove(key, out var tcs))
                        tcs.TrySetResult(files);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // Object message with "type"
                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var type = typeProp.GetString() ?? "";
                    var key = $"{device.Id}:{type}";

                    if (type == "ERROR")
                    {
                        var errMsg = root.GetProperty("message").GetString();
                        foreach (var k in _pendingRequests.Keys.Where(k => k.StartsWith(device.Id)))
                        {
                            if (_pendingRequests.TryRemove(k, out var pendingTcs))
                                pendingTcs.TrySetException(new Exception(errMsg));
                        }
                    }
                    else if (type == "success")
                    {
                        // Map success messages to the correct request type
                        string[] possibleKeys = { "DELETE_FILE", "DELETE_ALL_FILES", "DISCONNECT" };
                        foreach (var keyName in possibleKeys)
                        {
                            var _key = $"{device.Id}:{keyName}";
                            if (_pendingRequests.TryRemove(_key, out var tcs))
                            {
                                object response = keyName switch
                                {
                                    "DELETE_FILE" => new DeleteFileResponse { Success = true, Message = root.GetProperty("message").GetString() },
                                    "DELETE_ALL_FILES" => new DeleteAllFilesResponse { Success = true },
                                    "DISCONNECT" => new DisconnectResponse { Success = true, Message = root.GetProperty("message").GetString() },
                                    _ => null
                                };
                                if (response != null)
                                    tcs.TrySetResult(response);
                            }
                        }
                    }

                    else
                    {
                        // Handle STATUS or DELETE_ALL_FILES
                        object parsed = type switch
                        {
                            "STATUS" => new DeviceStatusInfo
                            {
                                Uptime = root.TryGetProperty("uptime", out var uptime) ? uptime.GetString() : null,
                                Heap = root.TryGetProperty("heap", out var heap) ? heap.GetString() : null,
                                Wifi_Rssi = root.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetString() : null,
                                Wifi_Ip = root.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
                                Connected = root.TryGetProperty("connected", out var connected) && connected.GetBoolean()
                            },
                            "DELETE_ALL_FILES" => new DeleteAllFilesResponse
                            {
                                Success = root.TryGetProperty("success", out var s) && s.GetBoolean()
                            },
                            _ => null
                        };

                        if (parsed != null && _pendingRequests.TryRemove(key, out var tcs))
                            tcs.TrySetResult(parsed);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message from {device.Id}: {ex.Message}");
            }
        }
    }


    private List<FileInfoModel> ParseFileList(JsonDocument doc)
    {
        var filesList = new List<FileInfoModel>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                filesList.Add(new FileInfoModel
                {
                    Name = f.GetProperty("name").GetString() ?? "",
                    Size = f.GetProperty("size").GetInt32()
                });
            }
        }
        return filesList;
    }


    #endregion


    #region Handler Method Per Endpoint

    public async Task<DeviceStatusInfo> RequestDeviceStatusAsync(string deviceId) =>
    await SendRequestAsync(deviceId, "STATUS", "{\"type\":\"STATUS\"}", doc => new DeviceStatusInfo
    {
        Uptime = doc.RootElement.TryGetProperty("uptime", out var uptime) ? uptime.GetString() : null,
        Heap = doc.RootElement.TryGetProperty("heap", out var heap) ? heap.GetString() : null,
        Wifi_Rssi = doc.RootElement.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetString() : null,
        Wifi_Ip = doc.RootElement.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
        Connected = doc.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean()
    });

    public async Task<List<FileInfoModel>> RequestFileListAsync(string deviceId)
    {
        var requestJson = "{\"type\":\"LIST_FILES\"}";
        return await SendRequestAsync<List<FileInfoModel>>(
            deviceId,
            "LIST_FILES",
            requestJson,
            doc =>
            {
                // This parser is not actually used now because arrays are handled in receive loop,
                // but required by SendRequestAsync<T> signature
                return new List<FileInfoModel>();
            }
        );
    }


    public async Task<DeleteAllFilesResponse> DeleteAllFilesAsync(string deviceId) =>
        await SendRequestAsync(deviceId, "DELETE_ALL_FILES", "{\"type\":\"DELETE_ALL_FILES\"}",
            doc => new DeleteAllFilesResponse
            {
                Success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean()
            });

    public async Task<DeleteFileResponse> DeleteFileAsync(string deviceId, string fileName)
    {
        var requestJson = $"{{\"type\":\"DELETE_FILE\",\"filename\":\"{fileName}\"}}";

        return await SendRequestAsync<DeleteFileResponse>(
            deviceId,
            "DELETE_FILE",
            requestJson,
            doc => new DeleteFileResponse
            {
                Success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean(),
                Message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null
            });
    }

    public async Task<DisconnectResponse> DisconnectDeviceAsync(string deviceId)
    {
        var requestJson = "{\"type\":\"DISCONNECT\"}";

        return await SendRequestAsync<DisconnectResponse>(
            deviceId,
            "DISCONNECT",
            requestJson,
            doc => new DisconnectResponse
            {
                Success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean(),
                Message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null
            });
    }



    #endregion


    //#region Status
    //public async Task StartReceiveLoopAsync(string deviceId, CancellationToken ct)
    //{
    //    var device = _deviceManager.GetDevice(deviceId);
    //    var ws = device.Socket;
    //    var buffer = new byte[16 * 1024];

    //    try
    //    {
    //        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    //        {
    //            var jsonString = await ReceiveFullMessageAsync(ws, buffer, ct);

    //            using var doc = JsonDocument.Parse(jsonString);
    //            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) continue;

    //            var type = typeProp.GetString();

    //            if (type == "STATUS")
    //            {
    //                var status = new DeviceStatusInfo
    //                {
    //                    Uptime = doc.RootElement.TryGetProperty("uptime", out var uptime) ? uptime.GetRawText() : null,
    //                    Heap = doc.RootElement.TryGetProperty("heap", out var heap) ? heap.GetRawText() : null,
    //                    Wifi_Rssi = doc.RootElement.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetRawText() : null,
    //                    Wifi_Ip = doc.RootElement.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
    //                    Connected = doc.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean()
    //                };

    //                device.Status = status;

    //                // Resolve any waiting request
    //                if (_pendingStatusRequests.TryRemove(device.Id, out var tcs))
    //                    tcs.TrySetResult(status);
    //            }
    //            else if (type == "ERROR")
    //            {
    //                var errMsg = doc.RootElement.GetProperty("message").GetString();
    //                if (_pendingStatusRequests.TryRemove(device.Id, out var tcs))
    //                    tcs.TrySetException(new Exception(errMsg));
    //            }
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine($"Receive loop failed: {ex.Message}");
    //    }
    //}


    //private async Task<string> ReceiveFullMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    //{
    //    using var ms = new MemoryStream();
    //    WebSocketReceiveResult result;
    //    do
    //    {
    //        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
    //        if (result.MessageType == WebSocketMessageType.Close)
    //            throw new WebSocketException("Socket closed while receiving message.");
    //        ms.Write(buffer, 0, result.Count);
    //    } while (!result.EndOfMessage);

    //    return Encoding.UTF8.GetString(ms.ToArray());

    //}

    //public async Task<DeviceStatusInfo> RequestDeviceStatusAsync(string deviceId)
    //{
    //    var device = _deviceManager.GetDevice(deviceId);
    //    if (device == null || device.Socket.State != WebSocketState.Open)
    //        throw new Exception("Device not connected.");

    //    var tcs = new TaskCompletionSource<DeviceStatusInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    //    _pendingStatusRequests[device.Id] = tcs;

    //    var requestJson = "{\"type\":\"STATUS\"}";
    //    await device.Socket.SendAsync(
    //        Encoding.UTF8.GetBytes(requestJson),
    //        WebSocketMessageType.Text,
    //        true,
    //        CancellationToken.None);

    //    return await tcs.Task; // Will complete when receive loop dispatches
    //}



    //#endregion

    //#region File List
    //// for file list 

    //public async Task<List<FileInfoModel>> RequestFileListAsync(string deviceId)
    //{
    //    return await SendFileListRequestAsync(deviceId, "{\"type\":\"LIST_FILES\"}", doc =>
    //    {
    //        var filesList = new List<FileInfoModel>();
    //        if (doc.RootElement.ValueKind == JsonValueKind.Array)
    //        {
    //            foreach (var f in doc.RootElement.EnumerateArray())
    //            {
    //                filesList.Add(new FileInfoModel
    //                {
    //                    Name = f.GetProperty("name").GetString(),
    //                    Size = f.GetProperty("size").GetInt32()
    //                });
    //            }
    //        }
    //        return filesList;
    //    });
    //}

    //private async Task<T> SendFileListRequestAsync<T>(
    //string deviceId,
    //string requestJson,
    //Func<JsonDocument, T> parseResponse)
    //{
    //    var device = _deviceManager.GetDevice(deviceId);
    //    if (device == null || device.Socket.State != WebSocketState.Open)
    //        throw new Exception("Device not connected.");

    //    var ws = device.Socket;
    //    var tcs = new TaskCompletionSource<T>();
    //    var buffer = new byte[16 * 1024];

    //    async Task HandleResponse(WebSocketReceiveResult result)
    //    {
    //        if (result.MessageType != WebSocketMessageType.Text) return;

    //        var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
    //        using var doc = JsonDocument.Parse(msg);

    //        if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
    //            typeProp.GetString() == "ERROR")
    //        {
    //            var errMsg = doc.RootElement.GetProperty("message").GetString();
    //            tcs.TrySetException(new Exception(errMsg));
    //        }
    //        else
    //        {
    //            var parsed = parseResponse(doc);
    //            tcs.TrySetResult(parsed);
    //        }
    //    }

    //    // Send request
    //    await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson),
    //        WebSocketMessageType.Text, true, CancellationToken.None);

    //    // Wait for response
    //    while (!tcs.Task.IsCompleted)
    //    {
    //        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    //        await HandleResponse(result);
    //    }

    //    return await tcs.Task;
    //}

    //#endregion

    // other 
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


    //public async Task<DeviceStatusInfo> RequestDeviceStatusAsync(string deviceId)
    //{
    //    var device = _deviceManager.GetDevice(deviceId);
    //    if (device == null || device.Socket.State != WebSocketState.Open)
    //        throw new Exception("Device not connected.");
    //    var ws = device.Socket;
    //    var tcs = new TaskCompletionSource<DeviceStatusInfo>();
    //    // Helper to receive a full message (handles fragmentation) 
    //    async Task<string> ReceiveFullMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    //    {
    //        using var ms = new MemoryStream(); WebSocketReceiveResult result;
    //        do
    //        {
    //            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
    //            if (result.MessageType == WebSocketMessageType.Close)
    //                throw new WebSocketException("Socket closed while receiving message.");
    //            ms.Write(buffer, 0, result.Count);
    //        } while (!result.EndOfMessage);
    //        return Encoding.UTF8.GetString(ms.ToArray());
    //    }
    //    // Send STATUS request 
    //    var requestJson = "{\"type\":\"STATUS\"}";
    //    await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);
    //    var buffer = new byte[16 * 1024];
    //    while (!tcs.Task.IsCompleted)
    //    {
    //        var jsonString = await ReceiveFullMessageAsync(ws, buffer, CancellationToken.None);
    //        using var doc = JsonDocument.Parse(jsonString);
    //        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) continue;
    //        var type = typeProp.GetString() ?? "";
    //        Console.WriteLine(type);
    //        if (type == "STATUS")
    //        {
    //            Console.WriteLine("jsonString");
    //            var status = new DeviceStatusInfo
    //            {
    //                Uptime = doc.RootElement.TryGetProperty("uptime", out var uptime) ? uptime.GetRawText() : null,
    //                Heap = doc.RootElement.TryGetProperty("heap", out var heap) ? heap.GetRawText() : null,
    //                Wifi_Rssi = doc.RootElement.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetRawText() : null,
    //                Wifi_Ip = doc.RootElement.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
    //                Connected = doc.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean()
    //            };
    //            device.Status = status; tcs.TrySetResult(status);
    //        }
    //        else if (type == "ERROR")
    //        {
    //            var errMsg = doc.RootElement.GetProperty("message").GetString();
    //            tcs.TrySetException(new Exception(errMsg));
    //        }
    //    }
    //    return await tcs.Task;
    //}

   

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
