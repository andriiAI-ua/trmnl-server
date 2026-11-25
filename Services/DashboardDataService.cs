using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

public sealed class DisplayOptions
{
    public const string Section = "Display";

    public const string DashboardLayout = "Dashboard";
    public const string DaysInYearLayout = "DaysInYear";

    // Dashboard: landscape dashboard. DaysInYear: the old Markdown screen.
    public string Layout { get; set; } = DashboardLayout;
}

public sealed class DashboardOptions
{
    public const string Section = "Dashboard";

    // False: 1-bit black and white image, the device can do a partial refresh.
    // True: 4 gray levels, the device always does a full refresh.
    public bool Grayscale { get; set; }

    public string PowerQueue { get; set; } = "";

    // Each list has the primary entity first, then the fallback entities.
    public string[] Temperature { get; set; } = [];
    public string[] Humidity { get; set; } = [];
    public string[] AirQuality { get; set; } = [];
    public string[] Voltage { get; set; } = [];

    public string DroneAlert { get; set; } = "";
    public string MissileAlert { get; set; } = "";

    // NUT status of the UPS: "OL" means input power (from the grid or the generator), "OB" means
    // power from the battery. It tells if the flat has power, not the source of the power.
    public string UpsStatus { get; set; } = "";

    public string NextPowerOff { get; set; } = "";
    public string NextPowerOn { get; set; } = "";

    // WMO index of the meteo.gov.ua station for the forecast, for example 33345 (Kyiv). Empty: no forecast.
    public string ForecastStation { get; set; } = "";

    // The city and the region of the 10-day forecast of meteo.gov.ua, as the forecast page of the site names
    // them, for example "Київ" and "Київська". An empty city: no "5 квадратів" and "10 днів" screens.
    public string ForecastCity { get; set; } = "";
    public string ForecastRegion { get; set; } = "";

    // The room climate for the "Кімната" and "Годинник" screens, for example the sensors of the RLCD device.
    // Each list has the primary entity first, then the fallback entities. Empty: no "Кімната" screen.
    public string[] RoomTemperature { get; set; } = [];
    public string[] RoomHumidity { get; set; } = [];

    public bool HasRoom => RoomTemperature.Any(e => !string.IsNullOrWhiteSpace(e))
        || RoomHumidity.Any(e => !string.IsNullOrWhiteSpace(e));

    // An input voltage below this value means that the grid has no power.
    public double GridOnVoltage { get; set; } = 100;

    public double BatteryEmptyVoltage { get; set; } = 3.3;
    public double BatteryFullVoltage { get; set; } = 4.1;
    public int BatteryLowPercent { get; set; } = 20;

    // Stop the Home Assistant requests after this time and show the screen without the missing data.
    public int TimeoutSeconds { get; set; } = 8;
}

public sealed record SensorValue(double Value, double? DayChange);

public sealed record AlertStatus(bool Drone, bool Missile, DateTimeOffset? Since)
{
    public bool Active => Drone || Missile;
}

/// <param name="DtekOn">Power from the grid (DTEK): from the power webhook (api/power/status). Null if not known.</param>
/// <param name="FlatPowered">
/// Power in the flat from the grid or from the generator: from the UPS status ("OL" or "OB"),
/// else from the UPS input voltage. Null if not known. The UPS cannot tell the source.
/// </param>
/// <param name="ScheduleKnown">True if Home Assistant returned the schedule entities.</param>
public sealed record PowerStatus(
    bool? DtekOn,
    bool? FlatPowered,
    double? Voltage,
    DateTimeOffset? NextOff,
    DateTimeOffset? NextOn,
    bool ScheduleKnown);

public sealed record BatteryStatus(double Voltage, int Percent, bool Low);

/// <param name="Temperature">Degrees Celsius, or null.</param>
/// <param name="Humidity">Percent, or null.</param>
public sealed record RoomClimate(double? Temperature, double? Humidity);

/// <param name="Room">The room climate, or null if the configuration has no room entities.</param>
/// <param name="Days">The 10-day forecast from today, or null if it was not asked for or is not available.</param>
public sealed record DashboardData(
    DateTime Now,
    string PowerQueue,
    SensorValue? Temperature,
    SensorValue? Humidity,
    SensorValue? AirQuality,
    ForecastDay? Tomorrow,
    AlertStatus? Alert,
    PowerStatus Power,
    BatteryStatus? Battery,
    RoomClimate? Room = null,
    IReadOnlyList<CityForecastDay>? Days = null);

/// <summary>
/// Collects the dashboard data from Home Assistant and from the device request.
/// </summary>
public sealed class DashboardDataService
{
    private readonly HomeAssistantClient _ha;
    private readonly MeteoForecastClient _meteo;
    private readonly MeteoCityForecastClient _cityForecast;
    private readonly PowerStatusStore _powerStatus;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<DashboardDataService> _log;

    public DashboardDataService(
        HomeAssistantClient ha,
        MeteoForecastClient meteo,
        MeteoCityForecastClient cityForecast,
        PowerStatusStore powerStatus,
        IOptionsMonitor<DashboardOptions> options,
        ILogger<DashboardDataService> log)
    {
        _ha = ha;
        _meteo = meteo;
        _cityForecast = cityForecast;
        _powerStatus = powerStatus;
        _options = options;
        _log = log;
    }

    /// <param name="forecastDays">Read also the 10-day forecast (the forecast screens).</param>
    public async Task<DashboardData> CollectAsync(
        decimal? batteryVoltage, CancellationToken cancellationToken, bool forecastDays = false)
    {
        var options = _options.CurrentValue;

        if (!_ha.Options.IsConfigured)
            _log.LogWarning("Home Assistant is not configured: set HomeAssistant:Url and HomeAssistant:Token");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var ct = cts.Token;

        var temperature = ReadSensorAsync(options.Temperature, ct);
        var humidity = ReadSensorAsync(options.Humidity, ct);
        var airQuality = ReadSensorAsync(options.AirQuality, ct);
        var voltage = ReadNumberAsync(options.Voltage, ct);
        var alert = ReadAlertAsync(options, ct);
        var nextOff = ReadStateAsync(options.NextPowerOff, ct);
        var nextOn = ReadStateAsync(options.NextPowerOn, ct);
        var ups = ReadStateAsync(options.UpsStatus, ct);
        var roomTemperature = ReadNumberAsync(options.RoomTemperature, ct);
        var roomHumidity = ReadNumberAsync(options.RoomHumidity, ct);
        // The forecasts come from memory: ForecastRefresher reads meteo.gov.ua in the background.
        var tomorrow = _meteo.GetForecast(DateTime.Today.AddDays(1));
        var days = forecastDays ? _cityForecast.GetDays(DateTime.Today) : null;

        await Task.WhenAll(temperature, humidity, airQuality, voltage, alert, nextOff, nextOn, ups,
            roomTemperature, roomHumidity);

        var now = DateTimeOffset.Now;
        var power = new PowerStatus(
            DtekOn: _powerStatus.Current?.On,
            FlatPowered: UpsHasPower(ups.Result) ?? (voltage.Result is { } v ? v >= options.GridOnVoltage : null),
            Voltage: voltage.Result,
            NextOff: FutureTime(nextOff.Result, now),
            NextOn: FutureTime(nextOn.Result, now),
            ScheduleKnown: nextOff.Result is not null || nextOn.Result is not null);

        return new DashboardData(
            Now: DateTime.Now,
            PowerQueue: options.PowerQueue,
            Temperature: temperature.Result,
            Humidity: humidity.Result,
            AirQuality: airQuality.Result,
            Tomorrow: tomorrow,
            Alert: alert.Result,
            Power: power,
            Battery: ToBattery(batteryVoltage, options),
            Room: options.HasRoom ? new RoomClimate(roomTemperature.Result, roomHumidity.Result) : null,
            Days: days);
    }

    private async Task<SensorValue?> ReadSensorAsync(string[] entities, CancellationToken ct)
    {
        var list = entities.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();
        foreach (var entity in list)
        {
            var value = await _ha.GetNumberAsync(entity, ct);
            if (value is null)
                continue;

            // Read the value of 24 hours ago from the same entity. If that entity was unavailable then,
            // read it from the other entities of the list, as the Telegram script did at that time.
            var dayAgo = await ReadDayAgoAsync(entity, ct);
            foreach (var other in list.Where(e => e != entity))
            {
                if (dayAgo is not null)
                    break;
                dayAgo = await ReadDayAgoAsync(other, ct);
            }

            return new SensorValue(value.Value, value - dayAgo);
        }

        return null;
    }

    // The value of 24 hours ago changes slowly. Keep it for this time.
    private static readonly TimeSpan DayAgoRefreshInterval = TimeSpan.FromMinutes(15);
    // After a failed read, try again after this time.
    private static readonly TimeSpan DayAgoRetryInterval = TimeSpan.FromMinutes(5);
    // Do not show a change from a value that is older than this time.
    private static readonly TimeSpan DayAgoMaxAge = TimeSpan.FromHours(1);

    private sealed record DayAgoEntry(double? Value, DateTime ValueAt, DateTime NextReadAt);

    private readonly ConcurrentDictionary<string, DayAgoEntry> _dayAgo = new();
    private readonly ConcurrentDictionary<string, Task> _dayAgoReads = new();

    /// <summary>
    /// Return the value that the entity had 24 hours ago. Read the history in the background and keep
    /// the value, because the history API can be slow. Wait for the read only until the token is cancelled.
    /// </summary>
    private async Task<double?> ReadDayAgoAsync(string entity, CancellationToken ct)
    {
        var entry = _dayAgo.GetValueOrDefault(entity);
        if (entry is null || DateTime.UtcNow >= entry.NextReadAt)
        {
            var read = _dayAgoReads.GetOrAdd(entity, e => Task.Run(() => ReadDayAgoInBackgroundAsync(e)));
            try
            {
                await read.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Show the screen without the new value. The next screen gets it.
            }

            entry = _dayAgo.GetValueOrDefault(entity);
        }

        return entry is not null && DateTime.UtcNow - entry.ValueAt <= DayAgoMaxAge ? entry.Value : null;
    }

    private async Task ReadDayAgoInBackgroundAsync(string entity)
    {
        try
        {
            // The history client has its own timeouts: this read does not use the timeout of the screen.
            var value = await _ha.GetNumberAtAsync(entity, DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None);
            var now = DateTime.UtcNow;
            var old = _dayAgo.GetValueOrDefault(entity);

            _dayAgo[entity] = value is not null
                ? new DayAgoEntry(value, now, now + DayAgoRefreshInterval)
                : new DayAgoEntry(old?.Value, old?.ValueAt ?? DateTime.MinValue, now + DayAgoRetryInterval);

            if (value is null)
                _log.LogInformation("No value of 24 hours ago for {Entity}", entity);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Reading the history of {Entity} failed: {Message}", entity, ex.Message);
            _dayAgo[entity] = new DayAgoEntry(null, DateTime.MinValue, DateTime.UtcNow + DayAgoRetryInterval);
        }
        finally
        {
            _dayAgoReads.TryRemove(entity, out _);
        }
    }

    private async Task<double?> ReadNumberAsync(string[] entities, CancellationToken ct)
    {
        foreach (var entity in entities.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var value = await _ha.GetNumberAsync(entity, ct);
            if (value is not null)
                return value;
        }

        return null;
    }

    private async Task<HaState?> ReadStateAsync(string entity, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(entity) ? null : await _ha.GetStateAsync(entity, ct);

    private async Task<AlertStatus?> ReadAlertAsync(DashboardOptions options, CancellationToken ct)
    {
        var drone = ReadStateAsync(options.DroneAlert, ct);
        var missile = ReadStateAsync(options.MissileAlert, ct);
        await Task.WhenAll(drone, missile);

        if (drone.Result is null && missile.Result is null)
            return null;

        var droneOn = IsOn(drone.Result);
        var missileOn = IsOn(missile.Result);

        // The alert starts at the earliest change of an active entity.
        var since = new[] { droneOn ? drone.Result : null, missileOn ? missile.Result : null }
            .Where(s => s?.LastChanged is not null)
            .Select(s => s!.LastChanged!.Value)
            .DefaultIfEmpty()
            .Min();

        return new AlertStatus(droneOn, missileOn, since == default ? null : since);
    }

    // Read a NUT status such as "OL", "OL CHRG" or "OB DISCHRG LB".
    // Return true if the UPS has input power (grid or generator), false if it works from the battery,
    // null if the status is not known.
    private static bool? UpsHasPower(HaState? state)
    {
        if (state is null)
            return null;

        var flags = state.State.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (flags.Contains("OB"))
            return false;
        if (flags.Contains("OL"))
            return true;
        return null;
    }

    private static bool IsOn(HaState? state) =>
        string.Equals(state?.State, "on", StringComparison.OrdinalIgnoreCase);

    // Return the time from a schedule entity. Ignore times in the past.
    private static DateTimeOffset? FutureTime(HaState? state, DateTimeOffset now)
    {
        if (state is null)
            return null;

        var time = ParseScheduleTime(state.State, state.Unit, state.LastChanged, now);
        return time > now ? time : null;
    }

    /// <summary>
    /// Get the time of the next power change from an entity state. Return null if the state has no time.
    /// The state can be a timestamp ("2026-11-11T12:00:00+00:00"), a number with a unit of time
    /// (the next_power_off and next_power_on sensors of Svitlo.live give hours: "6.8167" with the unit "h";
    /// Home Assistant shows it as "6h 49m") or a text such as "0h 12m".
    /// A number without a unit is minutes. A time to wait starts when the state changed.
    /// The result goes to the nearest 15 minutes: the schedules use 30-minute steps, and a state
    /// with 2 decimals of an hour ("6.79") has an error of some minutes.
    /// </summary>
    public static DateTimeOffset? ParseScheduleTime(string state, string? unit, DateTimeOffset? changedAt, DateTimeOffset now)
    {
        var text = state.Trim();
        if (text.Length == 0)
            return null;

        // Read a number first: DateTimeOffset.TryParse reads many numbers as dates
        // ("5.9" is 9 May, "5.9167" is 1 May 9167).
        TimeSpan wait;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            wait = unit?.Trim().ToLowerInvariant() switch
            {
                "d" => TimeSpan.FromDays(number),
                "h" => TimeSpan.FromHours(number),
                "s" => TimeSpan.FromSeconds(number),
                "ms" => TimeSpan.FromMilliseconds(number),
                _ => TimeSpan.FromMinutes(number) // "min" or no unit
            };
        }
        else
        {
            var m = DurationPattern.Match(text);
            if (!m.Success || !(m.Groups["d"].Success || m.Groups["h"].Success || m.Groups["m"].Success))
            {
                // A timestamp only in the ISO 8601 form of Home Assistant: "2026-11-11T16:30:00+00:00".
                return IsoTimestampPattern.IsMatch(text)
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                    ? time
                    : null;
            }

            wait = new TimeSpan(
                m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : 0,
                m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0,
                m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0,
                0);
        }

        var target = (changedAt ?? now) + wait;
        var step = TimeSpan.FromMinutes(15).Ticks;
        var rounded = (target.UtcTicks + step / 2) / step * step;
        return new DateTimeOffset(rounded, TimeSpan.Zero).ToOffset(target.Offset);
    }

    // "2026-11-11T16:30:00+00:00", "2026-11-11 16:30:00Z": a date with dashes and a time with colons.
    private static readonly Regex IsoTimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}",
        RegexOptions.CultureInvariant);

    // "1d 2h 3m", "0h 12m", "45m", "2 h 5 min". Each part is optional.
    private static readonly Regex DurationPattern = new(
        @"^(?:(?<d>\d+)\s*d\s*)?(?:(?<h>\d+)\s*h\s*)?(?:(?<m>\d+)\s*m(?:in)?\s*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static BatteryStatus? ToBattery(decimal? voltage, DashboardOptions options)
    {
        if (voltage is not > 0)
            return null;

        var v = (double)voltage.Value;
        var range = options.BatteryFullVoltage - options.BatteryEmptyVoltage;
        var percent = range > 0 ? (v - options.BatteryEmptyVoltage) / range * 100 : 100;
        var rounded = (int)Math.Clamp(Math.Round(percent), 0, 100);

        return new BatteryStatus(v, rounded, rounded <= options.BatteryLowPercent);
    }
}
