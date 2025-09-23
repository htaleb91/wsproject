using System.Collections.Concurrent;
using System.Net.WebSockets;

public class DeviceManager
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();

    public void AddDevice(string deviceId, WebSocket socket)
    {
        var device = new DeviceInfo
        {
            Id = deviceId,
            Socket = socket,
            Files = new List<FileInfoModel>()
        };
        _devices[deviceId] = device;
    }

    public void RemoveDevice(string deviceId)
    {
        _devices.TryRemove(deviceId, out _);
    }

    public DeviceInfo? GetDevice(string deviceId)
    {
        return _devices.TryGetValue(deviceId, out var device) ? device : null;
    }

    public IEnumerable<DeviceInfo> GetDevices()
    {
        return _devices.Values;
    }
}