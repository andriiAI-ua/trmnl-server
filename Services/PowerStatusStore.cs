using System.Text.Json;

/// <param name="On">True: the building has power. False: no power.</param>
/// <param name="ChangedAt">The time when the server got the status.</param>
public sealed record GridStatus(bool On, DateTimeOffset ChangedAt);

/// <summary>
/// Keeps the real power status of the building. healthchecks.io sees when a device in the building
/// stops or starts its pings; a webhook relay sends the status to trmnl-server.
/// The status is in Data/power-status.json, so it stays after a restart.
/// </summary>
public sealed class PowerStatusStore
{
    private readonly string _file;
    private readonly ILogger<PowerStatusStore> _log;
    private readonly object _sync = new();
    private GridStatus? _status;

    public PowerStatusStore(IHostEnvironment env, ILogger<PowerStatusStore> log)
    {
        _log = log;
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "power-status.json");
        _status = Load();
    }

    public GridStatus? Current
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public void Set(bool on)
    {
        var status = new GridStatus(on, DateTimeOffset.Now);
        lock (_sync)
        {
            _status = status;

            // Write a temporary file, then replace the old file. Then a stop during the write cannot damage it.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(status));
            File.Move(tmp, _file, overwrite: true);
        }

        _log.LogInformation("Power status of the building: {Status}", on ? "on" : "off");
    }

    private GridStatus? Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<GridStatus>(File.ReadAllText(_file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogWarning("Cannot read {File}: {Message}", _file, ex.Message);
            return null;
        }
    }
}
