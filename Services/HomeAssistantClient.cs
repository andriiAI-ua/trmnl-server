using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

public sealed class HomeAssistantOptions
{
    public const string Section = "HomeAssistant";

    public string Url { get; set; } = "";
    public string Token { get; set; } = "";

    // A sensor value older than this limit is stale.
    public int MaxAgeMinutes { get; set; } = 30;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);
}

/// <param name="Unit">The unit_of_measurement attribute, for example "h" or "min". Null if the entity has no unit.</param>
public sealed record HaState(
    string EntityId,
    string State,
    DateTimeOffset? LastChanged,
    DateTimeOffset? LastReported,
    string? Unit = null);

/// <summary>
/// Reads entity states from the Home Assistant REST API.
/// </summary>
public sealed class HomeAssistantClient
{
    public const string HttpClientName = "HomeAssistant";
    // The history API is slower than the states API. It has a client with longer timeouts
    // and its own circuit breaker, so a slow history does not stop the states.
    public const string HistoryHttpClientName = "HomeAssistantHistory";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<HomeAssistantOptions> _options;
    private readonly ILogger<HomeAssistantClient> _log;

    public HomeAssistantClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<HomeAssistantOptions> options,
        ILogger<HomeAssistantClient> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
    }

    public HomeAssistantOptions Options => _options.CurrentValue;

    /// <summary>
    /// Get the current state of an entity. Return null if the request fails.
    /// </summary>
    public async Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"api/states/{Uri.EscapeDataString(entityId)}", cancellationToken);
        if (doc is null)
            return null;

        var root = doc.RootElement;
        var state = root.TryGetProperty("state", out var s) ? s.ToString() : "";
        var lastChanged = ReadTime(root, "last_changed");
        // HA changes last_reported on each report, also when the value is the same.
        var lastReported = ReadTime(root, "last_reported") ?? ReadTime(root, "last_updated") ?? lastChanged;
        string? unit = null;
        if (root.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("unit_of_measurement", out var u)
            && u.ValueKind == JsonValueKind.String)
        {
            unit = u.GetString();
        }

        return new HaState(entityId, state, lastChanged, lastReported, unit);
    }

    /// <summary>
    /// Get the current numeric value of an entity.
    /// Return null if the entity is unavailable, not a number or stale.
    /// </summary>
    public async Task<double?> GetNumberAsync(string entityId, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(entityId, cancellationToken);
        if (state is null)
            return null;

        if (!TryParseNumber(state.State, out var value))
        {
            _log.LogInformation("Entity {Entity} is not available: {State}", entityId, state.State);
            return null;
        }

        var maxAge = TimeSpan.FromMinutes(Options.MaxAgeMinutes);
        if (state.LastReported is { } reported && DateTimeOffset.UtcNow - reported > maxAge)
        {
            _log.LogInformation("Entity {Entity} is stale: last report at {Reported}", entityId, reported);
            return null;
        }

        return value;
    }

    // The history window around the time. A sensor can be "unavailable" for a short time.
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Get the numeric value that an entity had at a time in the past.
    /// Look 1 hour before and after the time and take the nearest number: the last number before
    /// the time, else the first number after it. Return null if the window has no number.
    /// </summary>
    public async Task<double?> GetNumberAtAsync(string entityId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var from = at - HistoryWindow;
        var start = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = (at + HistoryWindow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var path =
            $"api/history/period/{start}?filter_entity_id={Uri.EscapeDataString(entityId)}" +
            $"&end_time={Uri.EscapeDataString(end)}&minimal_response&no_attributes";

        using var doc = await GetJsonAsync(path, cancellationToken, HistoryHttpClientName);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        (DateTimeOffset Time, double Value)? before = null, after = null;
        foreach (var series in doc.RootElement.EnumerateArray())
        {
            if (series.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in series.EnumerateArray())
            {
                if (!item.TryGetProperty("state", out var s) || !TryParseNumber(s.ToString(), out var value))
                    continue;

                // The first entry is the state at the start of the window; its change can be earlier.
                var time = ReadTime(item, "last_changed") ?? from;
                if (time <= at)
                {
                    if (before is null || time >= before.Value.Time)
                        before = (time, value);
                }
                else if (after is null || time < after.Value.Time)
                {
                    after = (time, value);
                }
            }
        }

        if ((before ?? after) is { } found)
            return found.Value;

        // Show the answer, so that the log tells why there is no number (for example, "unavailable").
        var raw = doc.RootElement.GetRawText();
        _log.LogInformation(
            "History of {Entity} has no number from {From:u} to {To:u}: {Answer}",
            entityId, from, at + HistoryWindow, raw.Length > 300 ? raw[..300] + "…" : raw);
        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(
        string relativePath, CancellationToken cancellationToken, string clientName = HttpClientName)
    {
        var options = Options;
        if (!options.IsConfigured)
            return null;

        try
        {
            var client = _httpFactory.CreateClient(clientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Url.TrimEnd('/')}/{relativePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Home Assistant request {Path} failed: HTTP {Status}", relativePath, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            // The circuit is open after many failures. The resilience handler logs the circuit state.
            _log.LogDebug("Home Assistant request {Path} skipped: the circuit is open", relativePath);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or ExecutionRejectedException)
        {
            _log.LogWarning("Home Assistant request {Path} failed: {Message}", relativePath, ex.Message);
            return null;
        }
    }

    private static DateTimeOffset? ReadTime(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return time;
        }

        return null;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
