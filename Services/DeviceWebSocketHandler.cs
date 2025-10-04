
using System.Net.Sockets;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.ModelBinding;

public class DeviceWebSocketHandler
{
    private readonly DeviceManager _deviceManager;

    //private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pendingRequests
    //= new();
    private readonly ConcurrentDictionary<(string RequestType, string DeviceId), TaskCompletionSource<object>> _pendingRequests
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
        var key = (requestType, deviceId);

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
                    if (result.Count == 0 || buffer[0] == 0x00)
                    {
                        Console.WriteLine("Received empty chunk - end of file confirmation.");
                        device.EmptyMessageRecieved = true;
                        break;
                        // Handle end-of-file logic here
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (ms.Length == 0)
                {
                    // Nothing to parse
                    continue; // go to next receive
                }
                var msg = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"Received from {device.Id}: {msg}");

                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                
                if (root.ValueKind == JsonValueKind.Array)
                {
                    // File list response
                    var files = root.EnumerateArray()
                                    .Select(f => new FileInfoModel
                                    {
                                        Name = f.GetProperty("name").GetString() ?? "",
                                        Size = f.GetProperty("size").GetInt32()
                                    })
                                    .ToList();

                    var key = ("LIST_FILES", device.Id);
                    if (_pendingRequests.TryRemove(key, out var tcs))
                        tcs.TrySetResult(files);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // Object messages
                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                    if (type == "ERROR")
                    {
                        var errMsg = root.GetProperty("message").GetString();
                        foreach (var k in _pendingRequests.Keys.ToList().Where(k => k.DeviceId == device.Id))
                        {
                            if (_pendingRequests.TryRemove(k, out var pendingTcs))
                                pendingTcs.TrySetException(new Exception(errMsg));
                        }
                    }
                    else if (type == "success")
                    {
                        // Map success messages to request types
                        string[] keys = { "DELETE_FILE", "DELETE_ALL_FILES", "DISCONNECT" };
                        foreach (var keyName in keys)
                        {
                            var _key = (keyName, device.Id);
                            if (_pendingRequests.TryRemove(_key, out var tcs))
                            {
                                object response = keyName switch
                                {
                                    "DELETE_FILE" => new DeleteFileResponse
                                    {
                                        Success = true,
                                        Message = root.TryGetProperty("message", out var m) ? m.GetString() : null
                                    },
                                    "DELETE_ALL_FILES" => new DeleteAllFilesResponse
                                    {
                                        Success = true
                                    },
                                    "DISCONNECT" => new DisconnectResponse
                                    {
                                        Success = true,
                                        Message = root.TryGetProperty("message", out var m) ? m.GetString() : null
                                    },

                                    "STATUS" => new DeviceStatusInfo
                                    {
                                        Uptime = root.TryGetProperty("uptime", out var uptime) ? uptime.GetString() : null,
                                        Heap = root.TryGetProperty("heap", out var heap) ? heap.GetString() : null,
                                        Wifi_Rssi = root.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetString() : null,
                                        Wifi_Ip = root.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
                                        Connected = root.TryGetProperty("connected", out var connected) && connected.GetBoolean()
                                    },
                                    _ => null
                                };
                                if (response != null)
                                    tcs.TrySetResult(response);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(type))
                    {
                        // Handle STATUS or other typed responses
                        var key = (type, device.Id);
                        object? parsed = type switch
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
    public async Task<FileDownloadResult> DownloadFileAsync(string deviceId, string fileName, string fileId)
    {
        //var count = _pendingRequests.Count(a => a.Key.Equals("FILE") && a.Value.Equals(deviceId));
        if (_pendingRequests.ContainsKey(("FILE", deviceId)))
        {
          
            throw new Exception("There is a download in progress");
        }
        var device = _deviceManager.GetDevice(deviceId)
                     ?? throw new Exception("Device not connected.");
        var ws = device.Socket;

        // Store chunks by index
        var chunks = new ConcurrentDictionary<int, byte[]>();
        string? receivedFileName = null;
        bool fileEnd = false;

        // TaskCompletionSource to await final file
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[("FILE", deviceId)] = tcs;

        // Send REQUEST_FILE
        var requestJson = $"{{\"type\":\"REQUEST_FILE\",\"filename\":\"{fileName}\"}}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!tcs.Task.IsCompleted)
                {
                    var (msgType, data) = await ReceiveFullMessage(ws, CancellationToken.None);

                    switch (msgType)
                    {
                        case WebSocketMessageType.Text:
                            if (data.Length == 0) break;

                            var msg = Encoding.UTF8.GetString(data);
                             var doc = JsonDocument.Parse(msg);
                            var type = doc.RootElement.GetProperty("type").GetString();

                            switch (type)
                            {
                                case "FILE_START":
                                    receivedFileName = doc.RootElement.GetProperty("filename").GetString();
                                    var ack = "{\"type\":\"FILE_START_ACK\"}";
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, CancellationToken.None);
                                    break;

                                case "FILE_END":
                                    fileEnd = true;

                                    //if (chunks.Count == 0)
                                    //    tcs.TrySetException(new Exception("No file chunks received."));
                                    //else
                                    {
                                        var fileData = chunks.OrderBy(c => c.Key)
                                                             .SelectMany(c => c.Value)
                                                             .ToArray();
                                        tcs.TrySetResult(new FileDownloadResult
                                        {
                                            FileName = receivedFileName ?? fileName,
                                            Data = fileData
                                        });

                                        // Notify device
                                        var complete = $"{{\"type\":\"COMPLETE\",\"fileId\":{fileId}}}";
                                        await ws.SendAsync(Encoding.UTF8.GetBytes(complete), WebSocketMessageType.Text, true, CancellationToken.None);
                                    }
                                    break;

                                case "ERROR":
                                    tcs.TrySetException(new Exception("Device error: " +
                                        doc.RootElement.GetProperty("message").GetString()));
                                    break;

                                    // Handle other text messages if needed
                            }
                            break;

                        case WebSocketMessageType.Binary:
                            // Ignore empty frames
                            if (data.Length <= 4) break;

                            // Extract chunk index
                            int index = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                            var chunk = new byte[data.Length - 4];
                            Array.Copy(data, 4, chunk, 0, chunk.Length);
                            chunks[index] = chunk;
                            break;

                        case WebSocketMessageType.Close:
                            tcs.TrySetException(new Exception("WebSocket closed during file download"));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _pendingRequests.TryRemove(("FILE", deviceId), out _);
            }
        });
        var resultObj = await tcs.Task;
        return (FileDownloadResult)resultObj;
        //return await tcs.Task;
    }

    //    public async Task<FileDownloadResult> DownloadFileAsync(string deviceId, string fileName, string fileId)
    //    {
    //        var ws = _deviceManager.GetDevice(deviceId)?.Socket
    //                 ?? throw new Exception("Device not connected.");

    //        // Use TaskCompletionSource<object> to match _pendingRequests dictionary
    //        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    //        var chunks = new ConcurrentDictionary<int, byte[]>();
    //        var failedChunks = new HashSet<int>();
    //        bool fileEnd = false;
    //        string? receivedFileName = null;
    //        var device = _deviceManager.GetDevice(deviceId);
    //        // Register pending request (boxed)
    //        _pendingRequests[("FILE", deviceId)] = tcs;

    //        // Send REQUEST_FILE to device
    //        var requestJson = $"{{\"type\":\"REQUEST_FILE\",\"filename\":\"{fileName}\"}}";
    //        await ws.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, CancellationToken.None);

    //        _ = Task.Run(async () =>
    //        {
    //            try
    //            {
    //                while (!tcs.Task.IsCompleted)
    //                {
    //                    var (msgType, data) = await ReceiveFullMessage(ws, CancellationToken.None);

    //                    if (msgType == WebSocketMessageType.Text)
    //                    {
    //                        if (data.Length == 0 || data[0] == 0x00)
    //                        {
    //                            continue;
    //                        }
    //                        var msg = Encoding.UTF8.GetString(data);
    //                        using var doc = JsonDocument.Parse(msg);
    //                        var type = doc.RootElement.GetProperty("type").GetString();

    //                        switch (type)
    //                        {
    //                            case "FILE_START":
    //                                receivedFileName = doc.RootElement.GetProperty("filename").GetString();
    //                                var ack = "{\"type\":\"FILE_START_ACK\"}";
    //                                await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, CancellationToken.None);
    //                                break;

    //                            case "FILE_END":
    //                                fileEnd = true;

    //                                // If no missing chunks, we can assemble the file immediately
    //                                if (failedChunks.Count == 0 && !tcs.Task.IsCompleted)
    //                                {
    //                                    var fileData = chunks.OrderBy(c => c.Key).SelectMany(c => c.Value).ToArray();
    //                                    tcs.TrySetResult((object)new FileDownloadResult
    //                                    {
    //                                        FileName = receivedFileName ?? fileName,
    //                                        Data = fileData
    //                                    });

    //                                    // Send COMPLETE to device
    //                                    var complete = $"{{\"type\":\"COMPLETE\",\"fileId\":{fileId}}}";
    //                                    await ws.SendAsync(Encoding.UTF8.GetBytes(complete), WebSocketMessageType.Text, true, CancellationToken.None);
    //                                }
    //                                break;

    //                            case "ack":
    //                                // Optionally handle ACK messages; file may already be completed
    //                                break;

    //                            case "ERROR":
    //                                tcs.TrySetException(new Exception("Device error: " + doc.RootElement.GetProperty("message").GetString()));
    //                                break;
    //                        }
    //                    }
    //                    else if (msgType == WebSocketMessageType.Binary)
    //                    {
    //                        if (device.EmptyMessageRecieved)
    //                        {
    //                            // Empty binary frame signals secondary completion
    //                            if (fileEnd)
    //                            {
    //                                if (failedChunks.Count == 0 && !tcs.Task.IsCompleted)
    //                                {
    //                                    var nonEmptyChunks = chunks
    //                                        .Where(kvp => kvp.Value != null && kvp.Value.Length > 0)
    //                                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    //                                    var fileData = nonEmptyChunks.OrderBy(c => c.Key).SelectMany(c => c.Value).ToArray();
    //                                    tcs.TrySetResult((object)new FileDownloadResult
    //                                    {
    //                                        FileName = receivedFileName ?? fileName,
    //                                        Data = fileData
    //                                    });

    //                                    var complete = $"{{\"type\":\"COMPLETE\",\"fileId\":{fileId}}}";
    //                                    await ws.SendAsync(Encoding.UTF8.GetBytes(complete), WebSocketMessageType.Text, true, CancellationToken.None);
    //                                }
    //                                else if (failedChunks.Count > 0)
    //                                {
    //                                    // Retry missing chunks
    //                                    string retryJson = JsonSerializer.Serialize(failedChunks);
    //                                    var retrySend = $"{{\"type\":\"RETRY\",\"chunks\":{retryJson}}}";
    //                                    await ws.SendAsync(Encoding.UTF8.GetBytes(retrySend), WebSocketMessageType.Text, true, CancellationToken.None);

    //                                    // Clear failedChunks for next retry
    //                                    failedChunks.Clear();
    //                                }
    //                            }
    //                        }
    //                        else
    //                        {
    //                            // Extract chunk index from first 4 bytes
    //                            int index = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
    //                            var chunk = new byte[data.Length - 4];
    //                            Array.Copy(data, 4, chunk, 0, chunk.Length);
    //                            chunks[index] = chunk;
    //                        }
    //                    }
    //                    else if (msgType == WebSocketMessageType.Close)
    //                    {
    //                        tcs.TrySetException(new Exception("WebSocket closed during file download"));
    //                    }
    //                }
    //            }
    //            catch (Exception ex)
    //            {

    //;                tcs.TrySetException(ex);
    //            }
    //            finally
    //            {
    //                _pendingRequests.TryRemove(("FILE", deviceId), out _);
    //            }
    //        });

    //        // Await the final file result and unbox
    //        var resultObj = await tcs.Task;
    //        return (FileDownloadResult)resultObj;
    //    }




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
                    //var device = _deviceManager.GetDevice(deviceId);
                    //_ = Task.Run(() => StartReceiveLoopAsync(device, CancellationToken.None));
                }
            }
        }
        finally
        {
            //_deviceManager.RemoveDevice(deviceId);
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
        var buffer = new byte[16 * 1024]; // bigger buffer for large chunks

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            // Console.WriteLine(result.Count);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("WebSocket closed during receive");
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
#endregion