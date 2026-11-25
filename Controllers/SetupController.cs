using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/setup")]
public class SetupController : ControllerBase
{
    private readonly DeviceRegistry _reg;
    private readonly ILogger<DisplayController> _log;

    public SetupController(DeviceRegistry r, ILogger<DisplayController> log)
    {
        _reg = r;
        _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Setup()
    {
        var headers = Request.Headers;
        _log.LogInformation("Request: {@Headers}", headers);

        var mac =
            headers["ID"].FirstOrDefault();

        if (mac == null)
            return NotFound();

        var dev =
            await _reg.GetByMac(mac)
            ?? await _reg.RegisterAsync(mac);

        return Ok(new {
            status = 200,
            api_key = dev.ApiKey,
            friendly_id = dev.FriendlyId,
            image_url = "",
            filename = "empty_state"
        });
    }
}