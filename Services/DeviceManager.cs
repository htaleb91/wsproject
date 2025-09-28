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
        if (_devices.TryRemove(deviceId, out var device))
        {
            device.Files.Clear(); // remove all files
            Console.WriteLine($"Device {deviceId} removed and its files cleared.");
        }
    }

    public void UpdateDeviceStatus(string deviceId, DeviceStatusInfo status)
    {
        _devices[deviceId].Status = status;
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