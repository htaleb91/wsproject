using System.Net.WebSockets;
using System.Collections.Concurrent;

public class DeviceConnectionManager
{
    // deviceId -> WebSocket
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    public bool AddDevice(string deviceId, WebSocket ws) =>
        _clients.TryAdd(deviceId, ws);

    public bool RemoveDevice(string deviceId) =>
        _clients.TryRemove(deviceId, out _);

    public IReadOnlyCollection<string> GetConnectedDeviceIds() =>
        _clients.Keys.ToList();

    public bool TryGetDevice(string deviceId, out WebSocket? ws) =>
        _clients.TryGetValue(deviceId, out ws);
}
