using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/log")]
public class LogController : ControllerBase
{
    private readonly ILogger<LogController> _log;

    public LogController(ILogger<LogController> log)
    {
        _log = log;
    }

    [HttpPost]
    public async Task<IActionResult> Log(CancellationToken cancellationToken)
    {
        // The device sends its own log when an operation fails. Write it to the server log.
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        _log.LogWarning(
            "Device log, Access-Token {AccessToken}: {Body}",
            Request.Headers["Access-Token"].FirstOrDefault(),
            body);

        return Ok();
    }
}
