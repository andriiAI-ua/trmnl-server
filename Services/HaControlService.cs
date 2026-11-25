using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Trmnl.Server.Models;

public sealed class HaMqttOptions
{
    public const string Section = "HaMqtt";
    public string? Broker { get; set; }
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// The TRMNL devices in Home Assistant (MQTT discovery), as the RLCD device: a select "Екран", a number
/// "Оновлення екрана" and a text "Текст", and the sensors of the device. The service keeps one connection
/// to the broker and takes the commands from Home Assistant. A new setting has effect at the next request
/// of the device: the device sleeps between the requests.
///
/// Topics: devices/&lt;id&gt;/&lt;key&gt; (the state, retained) and devices/&lt;id&gt;/&lt;key&gt;/set (the commands),
/// where &lt;id&gt; is the MAC address with "_" (see <see cref="DeviceScreenStore.DeviceKey"/>).
/// </summary>
public sealed class HaControlService : BackgroundService
{
    private const string DiscoveryPrefix = "homeassistant";
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Keep the Ukrainian names readable in the broker.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IMqttClient _client;
    private readonly HaMqttOptions _mqtt;
    private readonly DeviceScreenStore _store;
    private readonly IOptionsMonitor<DashboardOptions> _dashboard;
    private readonly ILogger<HaControlService> _log;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    // The firmware version of each device, for the device information in Home Assistant.
    private readonly ConcurrentDictionary<string, string> _firmware = new();
    private readonly IDisposable? _optionsChange;

    public HaControlService(
        IOptions<HaMqttOptions> mqtt,
        DeviceScreenStore store,
        IOptionsMonitor<DashboardOptions> dashboard,
        ILogger<HaControlService> log)
    {
        _mqtt = mqtt.Value;
        _store = store;
        _dashboard = dashboard;
        _log = log;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        // The list of the screens depends on the configuration (the city of the forecast, the room entities).
        _optionsChange = _dashboard.OnChange(_ => Task.Run(() => PublishAllAsync(CancellationToken.None)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_mqtt.Broker))
        {
            _log.LogInformation("MQTT is not configured (HaMqtt:Broker): no TRMNL devices in Home Assistant");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await ConnectAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning("MQTT connection to {Broker} failed: {Message}", _mqtt.Broker, ex.Message);
                }
            }

            try
            {
                await Task.Delay(ReconnectInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (_client.IsConnected)
                await _client.DisconnectAsync(cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogDebug("MQTT disconnect failed: {Message}", ex.Message);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Each host has its own client id: a second trmnl-server (for example, a standby copy) must not
        // disconnect this one.
        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"trmnl-server-{Environment.MachineName}")
            .WithTcpServer(_mqtt.Broker, _mqtt.Port)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrEmpty(_mqtt.Username))
            builder = builder.WithCredentials(_mqtt.Username, _mqtt.Password);

        await _client.ConnectAsync(builder.Build(), cancellationToken);
        _log.LogInformation("Connected to MQTT {Broker}:{Port}", _mqtt.Broker, _mqtt.Port);

        await _client.SubscribeAsync("devices/+/+/set", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
        // Home Assistant sends "online" after a start: then it needs the discovery again.
        await _client.SubscribeAsync($"{DiscoveryPrefix}/status", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
        await PublishAllAsync(cancellationToken);
    }

    /// <summary>
    /// Tell Home Assistant about a request of a device: its sensors and its settings. A new device gets its
    /// entities first. The call does not wait: MQTT is optional and the broker can be down.
    /// </summary>
    public void OnDisplay(TrmnlRequestHeaders headers, DeviceScreenSettings settings, bool isNew, int? batteryPercent)
    {
        var key = DeviceScreenStore.DeviceKey(headers.DeviceId);
        // The sw_version of the device changes with the firmware: then send the discovery again.
        var firmwareChanged = false;
        if (!string.IsNullOrWhiteSpace(headers.FirmwareVersion))
        {
            firmwareChanged = _firmware.GetValueOrDefault(key) != headers.FirmwareVersion;
            _firmware[key] = headers.FirmwareVersion;
        }

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!_client.IsConnected)
                    return;

                if (isNew || firmwareChanged)
                    await PublishDiscoveryAsync(key, settings, cts.Token);

                if (headers.FirmwareVersion is not null)
                    await PublishAsync(key, "firmware_version", headers.FirmwareVersion, cts.Token);
                if (headers.BatteryVoltage is { } voltage)
                    await PublishAsync(key, "battery_voltage", voltage.ToString("F2", CultureInfo.InvariantCulture), cts.Token);
                if (batteryPercent is { } percent)
                    await PublishAsync(key, "battery", percent.ToString(CultureInfo.InvariantCulture), cts.Token);
                if (headers.Rssi is { } rssi)
                    await PublishAsync(key, "rssi", rssi.ToString(CultureInfo.InvariantCulture), cts.Token);
                await PublishAsync(key, "last_request", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), cts.Token);
                // A standby trmnl-server can change the retained states: send them again.
                await PublishStatesAsync(key, settings, cts.Token);
            }
            catch (Exception ex)
            {
                _log.LogWarning("MQTT publish for {Device} failed: {Message}", key, ex.Message);
            }
        });
    }

    private async Task PublishAllAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
            return;

        try
        {
            foreach (var key in _store.DeviceKeys)
            {
                if (_store.Find(key) is not { } settings)
                    continue;
                await PublishDiscoveryAsync(key, settings, cancellationToken);
                await PublishStatesAsync(key, settings, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("MQTT discovery failed: {Message}", ex.Message);
        }
    }

    // The client calls this handler from its receive loop. A publish with QoS 1 waits for an answer from that
    // loop, so handle the message in another task.
    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
        _ = Task.Run(() => HandleMessageAsync(topic, payload));
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(string topic, string payload)
    {
        try
        {
            if (topic == $"{DiscoveryPrefix}/status")
            {
                if (payload == "online")
                    await PublishAllAsync(CancellationToken.None);
                return;
            }

            // devices/<id>/<key>/set
            var parts = topic.Split('/');
            if (parts is not ["devices", var key, var setting, "set"] || !_store.Contains(key))
                return;

            _log.LogInformation("MQTT command for {Device}: {Setting} = {Value}", key, setting, payload);
            var settings = setting switch
            {
                "screen" => ScreenCatalog.Find(payload) is { } kind && ScreenCatalog.IsAvailable(kind, _dashboard.CurrentValue)
                    ? _store.Update(key, s => s with { Screen = ScreenCatalog.Id(kind) })
                    : null,
                "refresh" => double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                    ? _store.Update(key, s => s with { RefreshMinutes = (int)Math.Round(minutes) })
                    : null,
                "note" => _store.Update(key, s => s with { Note = payload }),
                _ => null
            };

            // Send the state also for a wrong command: then Home Assistant shows the real value.
            if (_store.Find(key) is { } current)
                await PublishStatesAsync(key, settings ?? current, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning("MQTT command {Topic} failed: {Message}", topic, ex.Message);
        }
    }

    private async Task PublishStatesAsync(string key, DeviceScreenSettings settings, CancellationToken cancellationToken)
    {
        await PublishAsync(key, "status", "online", cancellationToken);
        await PublishAsync(key, "screen", ScreenCatalog.Name(_store.ScreenOf(settings)), cancellationToken);
        await PublishAsync(key, "refresh", settings.RefreshMinutes.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await PublishAsync(key, "note", settings.Note, cancellationToken);
    }

    private async Task PublishDiscoveryAsync(string key, DeviceScreenSettings settings, CancellationToken cancellationToken)
    {
        var root = $"devices/{key}";
        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { key },
            ["name"] = $"TRMNL {settings.Mac}",
            ["manufacturer"] = "TRMNL",
            ["sw_version"] = _firmware.GetValueOrDefault(key),
        };

        Dictionary<string, object?> Entity(string id, string name, params (string Key, object? Value)[] more)
        {
            var e = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["unique_id"] = $"{key}_{id}",
                ["availability_topic"] = $"{root}/status",
                ["device"] = device,
            };
            foreach (var (k, v) in more)
                e[k] = v;
            return e;
        }

        // The unique ids of the first 3 sensors are the same as in the old versions: Home Assistant keeps them.
        var entities = new (string Component, string Id, Dictionary<string, object?> Config)[]
        {
            ("sensor", "firmware_version", Entity("firmware_version", "Версія прошивки",
                ("state_topic", $"{root}/firmware_version"), ("entity_category", "diagnostic"))),
            ("sensor", "battery_voltage", Entity("battery_voltage", "Напруга батареї",
                ("state_topic", $"{root}/battery_voltage"), ("device_class", "voltage"), ("unit_of_measurement", "V"),
                ("state_class", "measurement"), ("entity_category", "diagnostic"))),
            ("sensor", "rssi", Entity("rssi", "Сигнал Wi-Fi",
                ("state_topic", $"{root}/rssi"), ("device_class", "signal_strength"), ("unit_of_measurement", "dBm"),
                ("state_class", "measurement"), ("entity_category", "diagnostic"))),
            ("sensor", "battery", Entity("battery", "Батарея",
                ("state_topic", $"{root}/battery"), ("device_class", "battery"), ("unit_of_measurement", "%"),
                ("state_class", "measurement"))),
            ("sensor", "last_request", Entity("last_request", "Останній запит",
                ("state_topic", $"{root}/last_request"), ("device_class", "timestamp"), ("icon", "mdi:sync"))),
            ("select", "screen", Entity("screen", "Екран",
                ("state_topic", $"{root}/screen"), ("command_topic", $"{root}/screen/set"),
                ("options", ScreenCatalog.Available(_dashboard.CurrentValue).Select(ScreenCatalog.Name).ToArray()),
                ("icon", "mdi:monitor-dashboard"))),
            ("number", "refresh", Entity("refresh", "Оновлення екрана",
                ("state_topic", $"{root}/refresh"), ("command_topic", $"{root}/refresh/set"),
                ("min", DeviceScreenStore.MinRefreshMinutes), ("max", DeviceScreenStore.MaxRefreshMinutes), ("step", 1),
                ("unit_of_measurement", "min"), ("mode", "box"), ("icon", "mdi:timer-sync-outline"),
                ("entity_category", "config"))),
            ("text", "note", Entity("note", "Текст",
                ("state_topic", $"{root}/note"), ("command_topic", $"{root}/note/set"),
                ("max", DeviceScreenStore.MaxNoteLength), ("mode", "text"), ("icon", "mdi:text"))),
        };

        foreach (var (component, id, config) in entities)
        {
            await PublishRawAsync($"{DiscoveryPrefix}/{component}/{key}_{id}/config",
                JsonSerializer.Serialize(config, JsonOptions), cancellationToken);
        }
    }

    private Task PublishAsync(string key, string name, string value, CancellationToken cancellationToken) =>
        PublishRawAsync($"devices/{key}/{name}", value, cancellationToken);

    // All messages are retained: Home Assistant gets the last values after its start.
    private async Task PublishRawAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            await _client.PublishAsync(message, cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public override void Dispose()
    {
        _optionsChange?.Dispose();
        _client.Dispose();
        _publishLock.Dispose();
        base.Dispose();
    }
}
