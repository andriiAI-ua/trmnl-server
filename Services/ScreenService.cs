using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

/// <summary>
/// Renders the screen of the configured layout and keeps it in the image cache.
/// The devices and the web page use it.
/// </summary>
public sealed class ScreenService
{
    // The web page gets the same screen for this time. Then many views do not send many requests to Home Assistant.
    private static readonly TimeSpan WebReuseTime = TimeSpan.FromMinutes(1);

    private readonly ImageCache _cache;
    private readonly DashboardDataService _dashboardData;
    private readonly DashboardRenderer _dashboardRenderer;
    private readonly DaysInYearRenderer _daysInYear;
    private readonly IOptionsMonitor<DisplayOptions> _displayOptions;
    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;
    private readonly ILogger<ScreenService> _log;

    // The last web screen for each variant: (compact, color).
    private readonly ConcurrentDictionary<(bool Compact, bool Color), (string Key, DateTime At)> _webScreens = new();

    public ScreenService(
        ImageCache cache,
        DashboardDataService dashboardData,
        DashboardRenderer dashboardRenderer,
        DaysInYearRenderer daysInYear,
        IOptionsMonitor<DisplayOptions> displayOptions,
        IOptionsMonitor<DashboardOptions> dashboardOptions,
        ILogger<ScreenService> log)
    {
        _cache = cache;
        _dashboardData = dashboardData;
        _dashboardRenderer = dashboardRenderer;
        _daysInYear = daysInYear;
        _displayOptions = displayOptions;
        _dashboardOptions = dashboardOptions;
        _log = log;
    }

    /// <summary>
    /// Render the screen for a device if the cache does not have it. Return the cache key.
    /// </summary>
    /// <param name="batteryVoltage">Battery voltage of the device. Null: do not show the battery.</param>
    /// <param name="width">Screen width of the device. A small screen gets the compact version.</param>
    /// <param name="height">Screen height of the device.</param>
    /// <param name="color">Render the dashboard in color (for Telegram). The compact screen is never in color.</param>
    public async Task<string> RenderAsync(
        decimal? batteryVoltage, int? width, int? height, CancellationToken cancellationToken, bool color = false)
    {
        var layout = _displayOptions.CurrentValue.Layout;
        // A small screen (for example, the 400x300 reflective LCD) gets the compact version of the layout.
        var compact = DashboardRenderer.IsCompact(width, height);

        if (!string.Equals(layout, DisplayOptions.DaysInYearLayout, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return await RenderDashboardAsync(batteryVoltage, compact, color && !compact, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Show the old screen if the dashboard fails: the device must always get an image.
                _log.LogError(ex, "Dashboard render failed, use the {Layout} layout", DisplayOptions.DaysInYearLayout);
            }
        }

        return await RenderDaysInYearAsync(compact, cancellationToken);
    }

    /// <summary>
    /// Render a screen of the list for a device if the cache does not have it. Return the cache key.
    /// The screens other than the dashboard and the days of the year are only 800x480: a small screen gets
    /// the dashboard.
    /// </summary>
    /// <param name="kind">The screen. A screen that the configuration cannot show gives the dashboard.</param>
    /// <param name="note">The text of the note screen.</param>
    public async Task<string> RenderAsync(
        ScreenKind kind, string note, decimal? batteryVoltage, int? width, int? height, CancellationToken cancellationToken)
    {
        var compact = DashboardRenderer.IsCompact(width, height);
        if (!ScreenCatalog.IsAvailable(kind, _dashboardOptions.CurrentValue))
            kind = ScreenKind.Dashboard;

        switch (kind)
        {
            case ScreenKind.DaysInYear:
                return await RenderDaysInYearAsync(compact, cancellationToken);
            case ScreenKind.Dashboard:
                return await RenderAsync(batteryVoltage, width, height, cancellationToken);
        }

        if (compact)
            return await RenderAsync(batteryVoltage, width, height, cancellationToken);

        try
        {
            var data = await _dashboardData.CollectAsync(
                batteryVoltage, cancellationToken, forecastDays: ScreenCatalog.NeedsForecastDays(kind));
            var view = _dashboardRenderer.BuildView(data, grayscale: _dashboardOptions.CurrentValue.Grayscale);
            var extras = new ScreenExtras(data.Now, data.Days, data.Room, note);

            var key = Hash($"{ScreenCatalog.Id(kind)}|{view.Fingerprint()}|{extras.Fingerprint(kind)}");
            if (!_cache.Exists(key))
            {
                var img = _dashboardRenderer.RenderScreen(kind, view, extras, DateTime.Now);
                await _cache.EnsureSavedAsync(key, img);
                _cache.PruneOldScreens();
            }

            return key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The device must always get an image.
            _log.LogError(ex, "Render of the {Screen} screen failed, use the dashboard", kind);
            return await RenderAsync(batteryVoltage, width, height, cancellationToken);
        }
    }

    /// <summary>
    /// Render the screen for the web page, without the battery. Return the cache key.
    /// Use the last screen if it is not older than 1 minute.
    /// </summary>
    public async Task<string> RenderForWebAsync(int? width, int? height, CancellationToken cancellationToken, bool color = false)
    {
        var compact = DashboardRenderer.IsCompact(width, height);
        var variant = (compact, color && !compact);
        if (_webScreens.TryGetValue(variant, out var last)
            && DateTime.UtcNow - last.At < WebReuseTime
            && _cache.Exists(last.Key))
        {
            return last.Key;
        }

        var key = await RenderAsync(batteryVoltage: null, width, height, cancellationToken, color);
        _webScreens[variant] = (key, DateTime.UtcNow);
        return key;
    }

    /// <summary>
    /// Render the screen of four quadrants for the RLCD device: a 1-bit BMP, 400x300, with an empty
    /// top left quadrant for the time of the device.
    /// </summary>
    public async Task<byte[]> RenderQuadAsync(CancellationToken cancellationToken)
    {
        var data = await _dashboardData.CollectAsync(batteryVoltage: null, cancellationToken);
        var view = _dashboardRenderer.BuildView(data, grayscale: false);
        return _dashboardRenderer.RenderQuad(view);
    }

    private async Task<string> RenderDashboardAsync(
        decimal? batteryVoltage, bool compact, bool color, CancellationToken cancellationToken)
    {
        var data = await _dashboardData.CollectAsync(batteryVoltage, cancellationToken);
        // The compact screen is a black and white LCD: it has no gray levels.
        var view = _dashboardRenderer.BuildView(
            data, grayscale: !compact && !color && _dashboardOptions.CurrentValue.Grayscale);

        // The key changes only when a shown value changes. Then the device does not redraw the same screen.
        var prefix = compact ? "compact|" : color ? "color|" : "";
        var key = Hash(prefix + view.Fingerprint());
        if (!_cache.Exists(key))
        {
            var img = _dashboardRenderer.Render(view, DateTime.Now, compact, color);
            await _cache.EnsureSavedAsync(key, img);
            _cache.PruneOldScreens();
        }

        return key;
    }

    private async Task<string> RenderDaysInYearAsync(bool compact, CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        var key = Hash(today.ToString("yyyyMMdd") + (compact ? "|compact" : ""));

        if (!_cache.Exists(key))
        {
            var img = compact
                ? await _daysInYear.RenderAsync(today, DashboardRenderer.CompactWidth, DashboardRenderer.CompactHeight, cancellationToken)
                : await _daysInYear.RenderAsync(today, DashboardRenderer.Width, DashboardRenderer.Height, cancellationToken);
            await _cache.EnsureSavedAsync(key, img);
            _cache.PruneOldScreens();
        }

        return key;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
