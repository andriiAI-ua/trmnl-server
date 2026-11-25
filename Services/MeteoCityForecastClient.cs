using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

/// <param name="Temperature">Degrees Celsius, rounded as on the site.</param>
/// <param name="Symbol">FMI SmartSymbol: 1..99 by day, 101..199 at night. 0: not known.</param>
public sealed record ForecastPoint(int Temperature, int Symbol);

/// <param name="Night">The value of 03:00, or null.</param>
/// <param name="Day">The value of 15:00, or null.</param>
public sealed record CityForecastDay(DateTime Date, ForecastPoint? Night, ForecastPoint? Day)
{
    [JsonIgnore]
    public bool Known => Night is not null || Day is not null;
}

/// <summary>
/// Reads the city forecast of meteo.gov.ua for 10 days: fmi.json, the data of the forecast page of the site.
/// The answer has a value for each hour. As the site, keep 03:00 for the night and 15:00 for the day.
/// The forecast is read after 06:00 and after 18:00, as on the RLCD device. <see cref="ForecastRefresher"/>
/// reads it in the background, and Data/city-forecast.json keeps it for a restart. Thus a screen does not wait
/// for the site, which is slow.
/// </summary>
public sealed class MeteoCityForecastClient
{
    public const int Days = 10;

    private const int NightHour = 3;
    private const int DayHour = 15;
    private static readonly int[] RefreshHours = [6, 18];
    // After a failed read, try again after this time.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
    // Do not show a forecast that is older than this time.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(36);

    private sealed record Snapshot(string Place, IReadOnlyDictionary<DateTime, CityForecastDay> Days, DateTime FetchedAt);

    // The format of Data/city-forecast.json.
    private sealed record StoredForecast(string Place, DateTime FetchedAt, List<CityForecastDay> Days);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<MeteoCityForecastClient> _log;
    private readonly string _file;

    private readonly object _sync = new();
    private Task _refresh = Task.CompletedTask;
    private string _attemptPlace = "";
    private DateTime _nextAttemptAt = DateTime.MinValue;
    private volatile Snapshot? _snapshot;

    public MeteoCityForecastClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<DashboardOptions> options,
        IHostEnvironment env,
        ILogger<MeteoCityForecastClient> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "city-forecast.json");
        _snapshot = Load();
    }

    /// <summary>True if the configuration has the city of the forecast.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.ForecastCity);

    /// <summary>
    /// Read the forecast if the last read is older than 06:00 or 18:00, or is for a different city.
    /// After a failed read, try again after 30 minutes. The task completes when the read completes.
    /// </summary>
    public Task RefreshIfDueAsync()
    {
        var place = Place();
        return place is null ? Task.CompletedTask : StartRefreshIfDue(place.Value);
    }

    /// <summary>
    /// Return 10 days from today. A day without values has null for the night and the day.
    /// Return null if the city is not configured or the forecast is not available.
    /// Do not wait for the site: <see cref="ForecastRefresher"/> reads it in the background.
    /// </summary>
    public IReadOnlyList<CityForecastDay>? GetDays(DateTime today)
    {
        var place = Place();
        if (place is null)
            return null;

        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Place != Key(place.Value) || DateTime.Now - snapshot.FetchedAt > MaxAge)
            return null;

        return Enumerable.Range(0, Days)
            .Select(i => today.Date.AddDays(i))
            .Select(d => snapshot.Days.GetValueOrDefault(d) ?? new CityForecastDay(d, null, null))
            .ToList();
    }

    private (string City, string Region)? Place()
    {
        var options = _options.CurrentValue;
        return string.IsNullOrWhiteSpace(options.ForecastCity)
            ? null
            : (options.ForecastCity.Trim(), options.ForecastRegion.Trim());
    }

    private static string Key((string City, string Region) place) => $"{place.City}|{place.Region}";

    // The last time of 06:00 or 18:00 before now (local time).
    private static DateTime LastRefreshPoint(DateTime now)
    {
        var points = RefreshHours.Select(h => now.Date.AddHours(h)).Where(t => t <= now).ToList();
        return points.Count > 0 ? points.Max() : now.Date.AddDays(-1).AddHours(RefreshHours.Max());
    }

    private Task StartRefreshIfDue((string City, string Region) place)
    {
        var key = Key(place);
        lock (_sync)
        {
            if (!_refresh.IsCompleted)
                return _refresh;

            var snapshot = _snapshot;
            var now = DateTime.Now;
            var fresh = snapshot is not null && snapshot.Place == key && snapshot.FetchedAt >= LastRefreshPoint(now);
            if (fresh || (key == _attemptPlace && DateTime.UtcNow < _nextAttemptAt))
                return Task.CompletedTask;

            _attemptPlace = key;
            _nextAttemptAt = DateTime.UtcNow + RetryInterval;
            _refresh = Task.Run(() => RefreshAsync(place));
            return _refresh;
        }
    }

    private async Task RefreshAsync((string City, string Region) place)
    {
        try
        {
            // The resilience handler of the HTTP client sets the retries and the total timeout.
            var client = _httpFactory.CreateClient(MeteoForecastClient.HttpClientName);
            var url = "https://www.meteo.gov.ua/fmi.json"
                + $"?city={Uri.EscapeDataString(place.City)}&region={Uri.EscapeDataString(place.Region)}"
                + "&lang=ua&action=getCityWeather";
            await using var stream = await client.GetStreamAsync(url);
            var days = await ParseAsync(stream, CancellationToken.None);
            if (days.Count == 0)
            {
                _log.LogWarning("meteo.gov.ua has no city forecast for {City}", place.City);
                return;
            }

            var snapshot = new Snapshot(Key(place), days, DateTime.Now);
            _snapshot = snapshot;
            _log.LogInformation("Loaded the city forecast from meteo.gov.ua: {Count} days", days.Count);
            Save(snapshot);
        }
        catch (BrokenCircuitException)
        {
            // The circuit is open after many failures. The resilience handler logs the circuit state.
            _log.LogDebug("meteo.gov.ua request skipped: the circuit is open");
        }
        catch (Exception ex)
        {
            // This is a background task: an exception must not stop the screen.
            _log.LogWarning("meteo.gov.ua city forecast request failed: {Message}", ex.Message);
        }
    }

    // Read the last forecast from the file. GetDays and StartRefreshIfDue check its city and its age.
    private Snapshot? Load()
    {
        if (!File.Exists(_file))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredForecast>(File.ReadAllText(_file), FileJsonOptions);
            if (stored is null || stored.Days.Count == 0)
                return null;

            _log.LogInformation("Read the city forecast of {FetchedAt:g} from {File}", stored.FetchedAt, _file);
            return new Snapshot(stored.Place, stored.Days.ToDictionary(d => d.Date.Date), stored.FetchedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot read {File}: {Message}", _file, ex.Message);
            return null;
        }
    }

    // Write a temporary file, then replace the old file. Then a stop during the write cannot damage it.
    private void Save(Snapshot snapshot)
    {
        try
        {
            var stored = new StoredForecast(snapshot.Place, snapshot.FetchedAt, snapshot.Days.Values.OrderBy(d => d.Date).ToList());
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(stored, FileJsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot write {File}: {Message}", _file, ex.Message);
        }
    }

    /// <summary>
    /// Read the days from the answer of fmi.json. "fulldata" has one object for each hour, also for past hours:
    /// {"time": "20261123T030000", "meantemp": 0.4, "SmartSymbol": 104, ...}. The time is local.
    /// A value that the site does not have is null.
    /// </summary>
    public static async Task<IReadOnlyDictionary<DateTime, CityForecastDay>> ParseAsync(
        Stream json, CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        var days = new Dictionary<DateTime, CityForecastDay>();
        if (!doc.RootElement.TryGetProperty("fulldata", out var list) || list.ValueKind != JsonValueKind.Array)
            return days;

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("time", out var timeValue)
                || timeValue.ValueKind != JsonValueKind.String
                || !DateTime.TryParseExact(timeValue.GetString(), "yyyyMMdd'T'HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                || time.Hour is not (NightHour or DayHour)
                || !entry.TryGetProperty("meantemp", out var tempValue)
                || tempValue.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var symbol = entry.TryGetProperty("SmartSymbol", out var symbolValue) && symbolValue.ValueKind == JsonValueKind.Number
                ? (int)symbolValue.GetDouble()
                : 0;
            // Round as Math.round() of the site: -2.5 gives -2.
            var point = new ForecastPoint((int)Math.Floor(tempValue.GetDouble() + 0.5), symbol);

            var date = time.Date;
            var day = days.GetValueOrDefault(date) ?? new CityForecastDay(date, null, null);
            days[date] = time.Hour == NightHour ? day with { Night = point } : day with { Day = point };
        }

        return days;
    }
}
