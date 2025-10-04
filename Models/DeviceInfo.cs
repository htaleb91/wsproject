using System.Net.WebSockets;

public class DeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public WebSocket? Socket { get; set; }
    public bool ReceiveLoopStarted { get; set; }
    public bool EmptyMessageRecieved { get; set; }
    public DeviceStatusInfo? Status { get; set; }
    public List<FileInfoModel> Files { get; set; } = new();
}


 public class DeviceStatusInfo
{

    public string Uptime { get; set; }
    public string Heap { get; set; }
    public string Wifi_Rssi { get; set; }
    public string Wifi_Ip { get; set; }
    public bool Connected { get; set; }
}
