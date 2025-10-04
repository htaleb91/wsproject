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
        return Ok(devices);
    }

    // Return last known local status (cached in Device object)
    [HttpGet("Device/localstatus/{deviceId}")]
    public IActionResult GetLocalStatus(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            return NotFound("Device not connected.");
        if (device.Status == null)
            return NotFound("Device status not updated yet.");
        return Json(device.Status);
    }

    // Ask device for fresh status
    [HttpGet("Device/status/{deviceId}")]
    public async Task<IActionResult> GetStatus(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            return NotFound("Device not connected.");

        try
        {
            var status = await _deviceWebSocketHandler.RequestDeviceStatusAsync(deviceId);
            return Json(status);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get status: {ex.Message}");
        }
    }

    // Ask device for file list
    [HttpGet("Device/files/{deviceId}")]
    public async Task<IActionResult> GetFiles(string deviceId)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null)
            return NotFound("Device not connected.");

        try
        {
            var files = await _deviceWebSocketHandler.RequestFileListAsync(deviceId);
            device.Files = files;
            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to get files: {ex.Message}");
        }
    }

    // Download a file from device
    [HttpGet("Device/download")]
    public async Task<IActionResult> Download(string deviceId, string filename)
    {
        var device = _deviceManager.GetDevice(deviceId);
        if (device == null) return NotFound("Device not connected.");

        var file = device.Files.FirstOrDefault(f => f.Name.Equals(filename));
        if (file == null) return NotFound("File not found.");

        try
        {
            var result = await _deviceWebSocketHandler.DownloadFileAsync(deviceId, file.Name);
            return File(result.Data, "application/octet-stream", result.FileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Download failed: {ex.Message}");
        }
    }

    // Delete all files
    [HttpPost("Device/deleteAll/{deviceId}")]
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
    }

    // Delete single file
    [HttpPost("Device/deleteFile/{deviceId}")]
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
    }

    // Disconnect device
    [HttpPost("Device/disconnect/{deviceId}")]
    public async Task<IActionResult> Disconnect(string deviceId)
    {
        try
        {
            var response = await _deviceWebSocketHandler.DisconnectDeviceAsync(deviceId);
            if (response.Success)
            {
                _deviceManager.RemoveDevice(deviceId);
                return Ok(new { success = true, message = response.Message });
            }
            return StatusCode(500, new { success = false, message = response.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // Show paginated file list in view
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
}
