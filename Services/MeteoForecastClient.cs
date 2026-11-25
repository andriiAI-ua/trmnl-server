using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

public sealed record ForecastDay(DateTime Date, int? Temperature, string Description);

/// <summary>
/// Reads the daily forecast from the weather informer of meteo.gov.ua (Ukrainian Hydrometeorological Center).
/// The informer is a script for web pages. It has the forecast for the next 3 days.
/// <see cref="ForecastRefresher"/> reads it in the background each hour, and Data/informer-forecast.json keeps
/// it for a restart. A screen only reads the last forecast: it does not wait for the site.
/// </summary>
public sealed partial class MeteoForecastClient
{
    public const string HttpClientName = "Meteo";

    // The forecast changes a few times in a day. Do not ask the site more frequently.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    // After a failed refresh, try again after this time. The circuit breaker can skip the call.
    private static readonly TimeSpan FailedRefreshInterval = TimeSpan.FromMinutes(15);
    // Show the last forecast for this period if the site is not available.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    // FetchedAt is UTC. The record is also the format of Data/informer-forecast.json.
    private sealed record Snapshot(string Station, IReadOnlyList<ForecastDay> Days, DateTime FetchedAt);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<MeteoForecastClient> _log;
    private readonly string _file;

    private readonly object _sync = new();
    private Task _refresh = Task.CompletedTask;
    private string _attemptStation = "";
    private DateTime _nextAttemptAt = DateTime.MinValue;
    private volatile Snapshot? _snapshot;

    public MeteoForecastClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<DashboardOptions> options,
        IHostEnvironment env,
        ILogger<MeteoForecastClient> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "informer-forecast.json");

        _snapshot = Load();
        if (_snapshot is not null)
        {
            // The forecast from the file is new enough until its next refresh time.
            _attemptStation = _snapshot.Station;
            _nextAttemptAt = _snapshot.FetchedAt + RefreshInterval;
        }
    }

    /// <summary>
    /// Return the last forecast for the date. Return null if the forecast is not available.
    /// Do not wait for the site: <see cref="ForecastRefresher"/> reads it in the background.
    /// </summary>
    public ForecastDay? GetForecast(DateTime date)
    {
        var station = _options.CurrentValue.ForecastStation;
        if (string.IsNullOrWhiteSpace(station))
            return null;

        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Station != station || DateTime.UtcNow - snapshot.FetchedAt > MaxAge)
            return null;

        return snapshot.Days.FirstOrDefault(d => d.Date == date.Date);
    }

    /// <summary>
    /// Read the forecast if the last read is older than 1 hour, or is for a different station.
    /// After a failed read, try again after 15 minutes. The task completes when the read completes.
    /// </summary>
    public Task RefreshIfDueAsync()
    {
        var station = _options.CurrentValue.ForecastStation;
        if (string.IsNullOrWhiteSpace(station))
            return Task.CompletedTask;

        lock (_sync)
        {
            if (!_refresh.IsCompleted)
                return _refresh;

            if (station == _attemptStation && DateTime.UtcNow < _nextAttemptAt)
                return Task.CompletedTask;

            _attemptStation = station;
            _nextAttemptAt = DateTime.UtcNow + FailedRefreshInterval;
            _refresh = Task.Run(() => RefreshAsync(station));
            return _refresh;
        }
    }

    private async Task RefreshAsync(string station)
    {
        try
        {
            // The resilience handler of the HTTP client sets the retries and the total timeout.
            var client = _httpFactory.CreateClient(HttpClientName);
            var script = await client.GetStringAsync(
                $"https://www.meteo.gov.ua/ua/informer/{Uri.EscapeDataString(station)}.js");

            var days = Parse(script, DateTime.Today);
            if (days.Count == 0)
            {
                _log.LogWarning("meteo.gov.ua informer for station {Station} has no forecast", station);
                return;
            }

            var snapshot = new Snapshot(station, days, DateTime.UtcNow);
            _snapshot = snapshot;
            lock (_sync)
                _nextAttemptAt = DateTime.UtcNow + RefreshInterval;

            _log.LogInformation("Loaded {Count} forecast days from meteo.gov.ua", days.Count);
            Save(snapshot);
        }
        catch (BrokenCircuitException)
        {
            // The circuit is open after many failures. The resilience handler logs the circuit state.
            _log.LogDebug("meteo.gov.ua request skipped: the circuit is open");
        }
        catch (Exception ex)
        {
            // This is a background task: an exception must not stop the server.
            _log.LogWarning("meteo.gov.ua request failed: {Message}", ex.Message);
        }
    }

    // Read the last forecast from the file. GetForecast checks its station and its age.
    private Snapshot? Load()
    {
        if (!File.Exists(_file))
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file), FileJsonOptions);
            if (stored is null || stored.Days.Count == 0)
                return null;

            var snapshot = stored with { FetchedAt = stored.FetchedAt.ToUniversalTime() };
            _log.LogInformation("Read the informer forecast of {FetchedAt:g} UTC from {File}", snapshot.FetchedAt, _file);
            return snapshot;
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
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, FileJsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot write {File}: {Message}", _file, ex.Message);
        }
    }

    /// <summary>
    /// Get the forecast days from the informer script. The script has a date (dd.MM),
    /// a weather description and a temperature for each day.
    /// </summary>
    public static IReadOnlyList<ForecastDay> Parse(string script, DateTime today)
    {
        var result = new List<ForecastDay>();

        foreach (Match m in ForecastDayPattern().Matches(script))
        {
            if (!DateTime.TryParseExact(m.Groups["dt"].Value, "dd.MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            // The informer does not give the year. A date before today is in the next year.
            date = new DateTime(today.Year, date.Month, date.Day);
            if (date < today.AddDays(-1))
                date = date.AddYears(1);

            var description = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
            var temperatureText = WebUtility.HtmlDecode(m.Groups["t"].Value).Replace("°", "").Trim();
            int? temperature = int.TryParse(temperatureText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t)
                ? t
                : null;

            result.Add(new ForecastDay(date, temperature, description));
        }

        return result;
    }

    [GeneratedRegex(
        @"<meteogovua-informer-pr-d-dt>(?<dt>[^<]+)</meteogovua-informer-pr-d-dt>.*?" +
        @"<meteogovua-informer-pr-d-icon\s+title=""(?<title>[^""]*)"".*?" +
        @"<meteogovua-informer-pr-d-t>(?<t>[^<]*)</meteogovua-informer-pr-d-t>",
        RegexOptions.Singleline)]
    private static partial Regex ForecastDayPattern();
}
