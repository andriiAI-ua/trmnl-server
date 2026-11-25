using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// Shows the dashboard image in the browser. Works only in the Development environment.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("dev/preview")]
public class PreviewController : ControllerBase
{
    private readonly IHostEnvironment _env;
    private readonly DashboardDataService _data;
    private readonly DashboardRenderer _renderer;
    private readonly DaysInYearRenderer _daysInYear;
    private readonly IOptionsMonitor<DashboardOptions> _options;

    public PreviewController(
        IHostEnvironment env,
        DashboardDataService data,
        DashboardRenderer renderer,
        DaysInYearRenderer daysInYear,
        IOptionsMonitor<DashboardOptions> options)
    {
        _env = env;
        _data = data;
        _renderer = renderer;
        _daysInYear = daysInYear;
        _options = options;
    }

    /// <param name="layout">Dashboard (default), DaysInYear, or Quad: the four quadrants of the RLCD device (BMP).</param>
    /// <param name="sample">
    /// Empty: real data from Home Assistant.
    /// calm, drone, missile, both, off, generator, scheduleoff, scheduleoff2, outage, nodata, lowbattery: sample data.
    /// </param>
    /// <param name="gray">Use 4 gray levels. Empty: use the configuration.</param>
    /// <param name="battery">Battery voltage of the device.</param>
    /// <param name="date">Date to show instead of today.</param>
    /// <param name="width">Screen width of the device, for example 400 for the compact dashboard.</param>
    /// <param name="height">Screen height of the device, for example 300 for the compact dashboard.</param>
    /// <param name="screen">
    /// A screen of the list (800x480 PNG): quad, quad5, days10, note, room, clock. It replaces the layout.
    /// </param>
    /// <param name="note">The text of the note screen. Empty with a sample: a sample text.</param>
    /// <param name="time">The time of the clock screen, for example 10:10.</param>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? layout,
        [FromQuery] string? sample,
        [FromQuery] bool? gray,
        [FromQuery] bool? color,
        [FromQuery] decimal? battery,
        [FromQuery] DateTime? date,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] string? screen,
        [FromQuery] string? note,
        [FromQuery] TimeSpan? time,
        CancellationToken cancellationToken)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        var compact = DashboardRenderer.IsCompact(width, height);

        if (string.Equals(layout, DisplayOptions.DaysInYearLayout, StringComparison.OrdinalIgnoreCase))
        {
            var (w, h) = compact
                ? (DashboardRenderer.CompactWidth, DashboardRenderer.CompactHeight)
                : (DashboardRenderer.Width, DashboardRenderer.Height);
            return File(await _daysInYear.RenderAsync(date ?? DateTime.Today, w, h, cancellationToken), "image/png");
        }

        var kind = ScreenCatalog.Find(screen);
        var data = string.IsNullOrWhiteSpace(sample)
            ? await _data.CollectAsync(battery ?? 3.95m, cancellationToken, forecastDays: kind is { } k && ScreenCatalog.NeedsForecastDays(k))
            : Sample(sample.ToLowerInvariant());

        if (date is { } d)
            data = data with { Now = d.Date + DateTime.Now.TimeOfDay };
        if (time is { } t)
            data = data with { Now = data.Now.Date + t };

        var view = _renderer.BuildView(data, gray ?? _options.CurrentValue.Grayscale);
        if (kind is { } screenKind and not (ScreenKind.Dashboard or ScreenKind.DaysInYear))
        {
            var text = note ?? (string.IsNullOrWhiteSpace(sample) ? "" : "Вода в банках на балконі. Ключі — у сусідки.");
            var extras = new ScreenExtras(data.Now, data.Days, data.Room, text);
            return File(_renderer.RenderScreen(screenKind, view, extras, DateTime.Now), "image/png");
        }
        if (string.Equals(layout, "quad", StringComparison.OrdinalIgnoreCase))
            return File(_renderer.RenderQuad(view), "image/bmp");

        var png = _renderer.Render(view, DateTime.Now, compact, color ?? false);

        return File(png, "image/png");
    }

    private DashboardData Sample(string sample)
    {
        var options = _options.CurrentValue;
        var now = DateTime.Now;
        var today = new DateTimeOffset(now.Date, DateTimeOffset.Now.Offset);

        var alert = sample switch
        {
            "drone" => new AlertStatus(true, false, DateTimeOffset.Now.AddMinutes(-23)),
            "missile" => new AlertStatus(false, true, DateTimeOffset.Now.AddMinutes(-7)),
            "both" => new AlertStatus(true, true, DateTimeOffset.Now.AddMinutes(-41)),
            "nodata" => null,
            _ => new AlertStatus(false, false, null)
        };

        var power = sample switch
        {
            // No DTEK and no generator: the UPS works from its battery.
            "off" => new PowerStatus(false, false, 0, null, today.AddHours(now.Hour + 3).AddMinutes(30), true),
            // No DTEK, the generator gives power.
            "generator" => new PowerStatus(false, true, 221, null, today.AddHours(now.Hour + 3).AddMinutes(30), true),
            // DTEK power is on, but the schedule has an outage now (until today or tomorrow).
            "scheduleoff" => new PowerStatus(true, true, 230, null, today.AddHours(now.Hour + 3).AddMinutes(30), true),
            "scheduleoff2" => new PowerStatus(true, true, 230, null, today.AddDays(1).AddHours(6), true),
            // No power, but the schedule has power now: an outage out of the schedule.
            "outage" => new PowerStatus(false, false, 0, today.AddHours(now.Hour + 4), null, true),
            "nodata" => new PowerStatus(null, null, null, null, null, false),
            "both" => new PowerStatus(true, true, 228, today.AddDays(1).AddHours(6), today.AddDays(1).AddHours(9).AddMinutes(30), true),
            // Svitlo.live gives only one time: the next power off when the power is on.
            _ => new PowerStatus(true, true, 232, today.AddHours(now.Hour + 2), null, true)
        };

        var battery = sample switch
        {
            "lowbattery" => new BatteryStatus(3.42, 15, true),
            "nodata" => null,
            _ => new BatteryStatus(3.95, 81, false)
        };

        return new DashboardData(
            Now: now,
            PowerQueue: options.PowerQueue,
            Temperature: sample == "nodata" ? null : new SensorValue(sample == "drone" ? -3.4 : 5.2, sample == "drone" ? -1.2 : 0.5),
            Humidity: sample == "nodata" ? null : new SensorValue(72, 15),
            AirQuality: sample == "nodata" ? null : new SensorValue(sample == "missile" ? 128 : 42, 3),
            Tomorrow: sample == "nodata" ? null : new ForecastDay(now.Date.AddDays(1), sample == "drone" ? -2 : 4, sample == "drone" ? "хмарно без опадів" : "хмарно дощ"),
            Alert: alert,
            Power: power,
            Battery: battery,
            Room: sample == "nodata" ? new RoomClimate(null, null) : new RoomClimate(21.4, 44),
            Days: sample == "nodata" ? null : SampleDays(now.Date));
    }

    // 10 days with different weather: the icons of all groups (FMI SmartSymbol).
    private static IReadOnlyList<CityForecastDay> SampleDays(DateTime today)
    {
        int[] symbols = [2, 31, 7, 51, 41, 1, 24, 71, 9, 54];
        int[] nights = [-1, 0, 2, -3, -5, -8, 1, 6, 3, -11];
        return symbols.Select((symbol, i) => new CityForecastDay(
                today.AddDays(i),
                new ForecastPoint(nights[i], 100 + (symbol == 1 ? 1 : symbol)),
                new ForecastPoint(nights[i] + 3, symbol)))
            .ToList();
    }
}
