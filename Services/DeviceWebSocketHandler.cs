using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using System;
using Microsoft.AspNetCore.SignalR;
using WsProjet.Services;
using System.Runtime.CompilerServices;

public class DeviceWebSocketHandler
{
    private readonly DeviceManager _deviceManager;
    private readonly IHubContext<DownloadHub> _hub;
    private readonly ConcurrentDictionary<(string RequestType, string DeviceId), TaskCompletionSource<object>> _pendingRequests = new();

    // Active file downloads
    private readonly ConcurrentDictionary<string, FileDownloadSession> _downloads = new();

    public DeviceWebSocketHandler(DeviceManager deviceManager, IHubContext<DownloadHub> hub)
    {
        _deviceManager = deviceManager;
        _hub = hub;
    }

    public async Task StartReceiveLoopAsync(DeviceInfo device, CancellationToken ct)
    {
        var ws = device.Socket;
        var buffer = new byte[16 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[{device.Id}] WebSocket closed");
                    return;
                }
                if (result.Count > 0)
                    ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = ms.ToArray();

            if (result.MessageType == WebSocketMessageType.Text)
                HandleTextMessage(device, Encoding.UTF8.GetString(data));
            else if (result.MessageType == WebSocketMessageType.Binary)
                await HandleBinaryMessage(device, data);
        }
    }
    private async Task HandleTextMessage(DeviceInfo device, string msg)
    {
        using var doc = JsonDocument.Parse(msg);

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            // Standard object with "type"
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();

                switch (type)
                {
                    case "STATUS":
                        var status = new DeviceStatusInfo
                        {
                            Uptime = doc.RootElement.TryGetProperty("uptime", out var uptime) ? uptime.GetString() : null,
                            Heap = doc.RootElement.TryGetProperty("heap", out var heap) ? heap.GetString() : null,
                            Wifi_Rssi = doc.RootElement.TryGetProperty("wifi_rssi", out var rssi) ? rssi.GetString() : null,
                            Wifi_Ip = doc.RootElement.TryGetProperty("wifi_ip", out var ip) ? ip.GetString() : null,
                            Connected = doc.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean()
                        };
                        device.Status = status;
                        CompleteRequest(("STATUS", device.Id), status);
                        break;

                    case "DELETE_FILE":
                        CompleteRequest(("DELETE_FILE", device.Id), (true, "File deleted"));
                        break;

                    case "DELETE_ALL_FILES":
                        CompleteRequest(("DELETE_ALL_FILES", device.Id), (true, "All files deleted"));
                        break;

                    case "ERROR":
                        var errMsg = doc.RootElement.GetProperty("message").GetString();
                        FailRequest(("STATUS", device.Id), new Exception(errMsg));
                        FailRequest(("LIST_FILES", device.Id), new Exception(errMsg));
                        FailRequest(("DELETE_FILE", device.Id), new Exception(errMsg));
                        FailRequest(("DELETE_ALL_FILES", device.Id), new Exception(errMsg));
                        break;

                    case "FILE_START":
                        var filename = doc.RootElement.GetProperty("filename").GetString();
                        var size = doc.RootElement.GetProperty("size").GetInt32();
                        Console.WriteLine($"File transfer starting: {filename} ({size} bytes)");

                        // <<< ADDED: start a new download session
                        var session = new FileDownloadSession(filename)
                        {
                            FileSize = size
                        };
                        _downloads[device.Id] = session;

                        CompleteRequest(("DOWNLOAD", device.Id), true); // optional if you want an ack
                        break;




                    case "FILE_END":
                        Console.WriteLine($"File transfer complete for {device.Id}");

                        // Assemble file if session exists
                        if (_downloads.TryRemove(device.Id, out var _session)) // <<< CHANGED
                        {
                            var fileBytes = _session.Assemble(); // <<< CHANGED
                            CompleteRequest(("FILE", device.Id), new FileDownloadResult // <<< CHANGED
                            {
                                Data = fileBytes,
                                FileName = _session.Filename
                            }); // <<< CHANGED
                        }
                        //_deviceManager.CompleteFile(device.Id); // keep existing
                        break;

                        // add other types as needed
                }
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // This is a file list
            var files = new List<FileInfoModel>();
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                files.Add(new FileInfoModel
                {
                    Name = f.GetProperty("name").GetString(),
                    Size = f.GetProperty("size").GetInt32(),
                    Id = f.TryGetProperty("id", out var idProp) ? idProp.ToString() : "0"
                });
            }

            device.Files = files;
            CompleteRequest(("LIST_FILES", device.Id), files);
        }
        else
        {
            Console.WriteLine($"⚠️ Unexpected JSON kind: {doc.RootElement.ValueKind}");
        }
    }
    private void FailRequest((string Key, string DeviceId) requestKey, Exception ex)
    {
        if (_pendingRequests.TryRemove(requestKey, out var tcs))
        {
            tcs.TrySetException(ex);
        }
    }

    private async Task HandleBinaryMessage(DeviceInfo device, byte[] data)
    {
        if (!_downloads.TryGetValue(device.Id, out var session))
            return;

        if (data.Length <= 4) return ;
        int index = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
        var chunk = new byte[data.Length - 4];
        Array.Copy(data, 4, chunk, 0, chunk.Length);
        session.Chunks[index] = chunk;
        Console.WriteLine($"Received chunk {index}, size={chunk.Length}"); // <<< ADDED: debug

        // Report progress via SignalR
        //int totalChunks = (int)Math.Ceiling((double)session.FileSize / session.ChunkSize);
        //int percent = (int)((session.Chunks.Count / (double)totalChunks) * 100);
        int receivedBytes = session.Chunks.Sum(c => c.Value.Length);
        int percent = session.FileSize > 0 ? (int)((receivedBytes / (double)session.FileSize) * 100) : 0;
        // Push to all clients (or filter by device/user if needed)
        await _hub.Clients.All.SendAsync("ReceiveDownloadProgress", device.Id, session.Filename, percent);

    }

    // --- Helpers ---
    private void CompleteRequest((string, string) key, object result)
    {
        if (_pendingRequests.TryRemove(key, out var tcs))
            tcs.TrySetResult(result);
    }

    private void FailAllRequests(string deviceId, Exception ex)
    {
        foreach (var key in _pendingRequests.Keys.Where(k => k.DeviceId == deviceId).ToList())
        {
            if (_pendingRequests.TryRemove(key, out var tcs))
                tcs.TrySetException(ex);
        }
    }


    // Request fresh status
    public async Task<DeviceStatusInfo> RequestDeviceStatusAsync(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            throw new InvalidOperationException("Device not connected.");

        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[("STATUS", deviceId)] = tcs;

        var msg = JsonSerializer.Serialize(new { type = "STATUS" });
        var buffer = Encoding.UTF8.GetBytes(msg);
        await device.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await tcs.Task;
        return (DeviceStatusInfo)result;
    }

    // Request file list
    public async Task<List<FileInfoModel>> RequestFileListAsync(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            throw new InvalidOperationException("Device not connected.");

        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[("LIST_FILES", deviceId)] = tcs;

        var msg = JsonSerializer.Serialize(new { type = "LIST_FILES" });
        var buffer = Encoding.UTF8.GetBytes(msg);
        await device.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await tcs.Task;
        return (List<FileInfoModel>)result;
    }

    // Download file (by name + ID)
    public async Task<FileDownloadResult> DownloadFileAsync(string deviceId, string filename)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            throw new InvalidOperationException("Device not connected.");

        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[("FILE", deviceId)] = tcs;

        var msg = JsonSerializer.Serialize(new { type = "REQUEST_FILE", filename });
        var buffer = Encoding.UTF8.GetBytes(msg);
        await device.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await tcs.Task;
        return (FileDownloadResult)result;
    }

    // Delete a single file
    public async Task<(bool Success, string Message)> DeleteFileAsync(string deviceId, string fileName)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            throw new InvalidOperationException("Device not connected.");

        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[("DELETE_FILE", deviceId)] = tcs;

        var msg = JsonSerializer.Serialize(new { type = "DELETE_FILE", filename = fileName });
        var buffer = Encoding.UTF8.GetBytes(msg);
        await device.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await tcs.Task;
        return ((bool Success, string Message))result;
    }

    // Delete all files
    public async Task<(bool Success, string Message)> DeleteAllFilesAsync(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            throw new InvalidOperationException("Device not connected.");

        var tcs = new TaskCompletionSource<object>();
        _pendingRequests[("DELETE_ALL_FILES", deviceId)] = tcs;

        var msg = JsonSerializer.Serialize(new { type = "DELETE_ALL_FILES" });
        var buffer = Encoding.UTF8.GetBytes(msg);
        await device.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await tcs.Task;
        return ((bool Success, string Message))result;
    }

    // Disconnect
    public async Task<(bool Success, string Message)> DisconnectDeviceAsync(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            return (false, "Device not connected.");

        await device.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected by server", CancellationToken.None);
        _deviceManager.RemoveDevice(deviceId);

        return (true, $"Device {deviceId} disconnected.");
    }

    private class FileDownloadSession
    {
        public string Filename { get; }
        public int FileSize { get; set; }
        public int ChunkSize { get; set; }
        public ConcurrentDictionary<int, byte[]> Chunks { get; } = new();
        public FileDownloadSession(string filename) => Filename = filename;

        public byte[] Assemble() =>
            Chunks.OrderBy(c => c.Key).SelectMany(c => c.Value).ToArray();
    }
}
