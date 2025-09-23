using System.Net.WebSockets;

public class DeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public WebSocket? Socket { get; set; }
    public List<FileInfoModel> Files { get; set; } = new();
}