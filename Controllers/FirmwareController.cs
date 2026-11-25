using Microsoft.AspNetCore.Mvc;
using Trmnl.Server.Services;

/// <summary>
/// Sends a firmware image to a device for the OTA update.
/// The URL comes from "firmware_url" of /api/display: /api/firmware/{file}?id={device ID}.
/// The server counts the downloads of each device to stop an update loop.
/// </summary>
[ApiController]
[Route("api/firmware")]
public class FirmwareController : ControllerBase
{
    private readonly FirmwareService _firmware;
    private readonly ILogger<FirmwareController> _log;

    public FirmwareController(FirmwareService firmware, ILogger<FirmwareController> log)
    {
        _firmware = firmware;
        _log = log;
    }

    [HttpGet("{file}")]
    public IActionResult Download(string file, [FromQuery] string? id)
    {
        var build = _firmware.FindBuild(file);

        if (build is null)
        {
            _log.LogWarning("Firmware: device {Device} asked for {File}: no such build", id, file);
            return NotFound();
        }

        var count = _firmware.RecordDownload(id, build);

        _log.LogInformation(
            "Firmware: device {Device} downloads {Version} ({File}, {Size} bytes), download {Count}",
            id, build.Version, build.FileName, build.Size, count);

        // The device needs Content-Length: Update.begin() gets the image size from it.
        return PhysicalFile(build.FullPath, "application/octet-stream");
    }
}
