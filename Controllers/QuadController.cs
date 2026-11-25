using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Returns the screen of four quadrants for the RLCD device (the "З сервера" screen of rlcd-dashboard):
/// a 1-bit BMP, 400x300. The top left quadrant is empty: the device draws its date, time and icons there.
/// The device reads it every 5 minutes on the home network, for example http://server:15000/quad.bmp.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("quad.bmp")]
public class QuadController : ControllerBase
{
    private readonly ScreenService _screens;

    public QuadController(ScreenService screens)
    {
        _screens = screens;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var bmp = await _screens.RenderQuadAsync(cancellationToken);

        // The screen changes: the device must get a new image each time.
        Response.Headers.CacheControl = "no-cache";
        return File(bmp, "image/bmp");
    }
}
