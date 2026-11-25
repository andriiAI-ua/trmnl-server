using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Returns the current screen as a PNG image for a web page.
/// nginx publishes only this path, for example as https://example.com/trmnl/screen.png.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("screen.png")]
public class ScreenController : ControllerBase
{
    private readonly ScreenService _screens;
    private readonly ImageCache _cache;

    public ScreenController(ScreenService screens, ImageCache cache)
    {
        _screens = screens;
        _cache = cache;
    }

    /// <param name="width">Screen width, for example 400 for the compact screen. Empty: 800x480.</param>
    /// <param name="height">Screen height, for example 300 for the compact screen.</param>
    /// <param name="color">True: the color 1600x960 image for Telegram.</param>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? width, [FromQuery] int? height, [FromQuery] bool? color, CancellationToken cancellationToken)
    {
        var key = await _screens.RenderForWebAsync(width, height, cancellationToken, color ?? false);

        // The screen changes: the browser must ask again each time.
        Response.Headers.CacheControl = "no-cache";
        return PhysicalFile(_cache.PathOf(key), "image/png");
    }
}
