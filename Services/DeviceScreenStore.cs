using System.Text;
using System.Text.Json;

/// <param name="Mac">The MAC address of the device (the "ID" header), for the name of the device in Home Assistant.</param>
/// <param name="Screen">The screen id (see <see cref="ScreenCatalog"/>).</param>
/// <param name="RefreshMinutes">How often the device asks for a new image.</param>
/// <param name="Note">The text of the "Текст" screen.</param>
public sealed record DeviceScreenSettings(string Mac, string Screen, int RefreshMinutes, string Note);

/// <summary>
/// Keeps the screen settings of each device: the screen, the refresh interval and the text. Home Assistant
/// changes them over MQTT. The file is Data/screens.json in the shared data folder, so a new release keeps it.
/// </summary>
public sealed class DeviceScreenStore
{
    public const int MinRefreshMinutes = 1;
    public const int MaxRefreshMinutes = 240;
    public const int DefaultRefreshMinutes = 5;
    public const int MaxNoteLength = 255;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly ScreenKind _defaultScreen;
    private readonly ILogger<DeviceScreenStore> _log;
    private readonly object _sync = new();
    private Dictionary<string, DeviceScreenSettings> _devices = new();
    // The last screen that each device got. This is only in memory: after a restart, the first screen is not a change.
    private readonly Dictionary<string, ScreenKind> _shown = new();

    public DeviceScreenStore(IHostEnvironment env, Microsoft.Extensions.Options.IOptions<DisplayOptions> display,
        ILogger<DeviceScreenStore> log)
    {
        _log = log;
        _defaultScreen = ScreenCatalog.Default(display.Value);
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "screens.json");
        Load();
    }

    /// <summary>
    /// The key of a device for the file and for MQTT: the MAC address with "_" in place of the other characters,
    /// for example "AA_BB_CC_DD_EE_FF". The old MQTT sensors of the device use the same id.
    /// </summary>
    public static string DeviceKey(string mac)
    {
        var sb = new StringBuilder(mac.Length);
        foreach (var c in mac)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    public IReadOnlyCollection<string> DeviceKeys
    {
        get
        {
            lock (_sync)
                return _devices.Keys.ToList();
        }
    }

    public bool Contains(string key)
    {
        lock (_sync)
            return _devices.ContainsKey(key);
    }

    /// <summary>Return the settings of the device, or null for an unknown device.</summary>
    public DeviceScreenSettings? Find(string key)
    {
        lock (_sync)
            return _devices.GetValueOrDefault(key);
    }

    /// <summary>
    /// Return the settings of the device. Add a new device with the default settings.
    /// </summary>
    /// <param name="isNew">True if the device was added now.</param>
    public DeviceScreenSettings GetOrAdd(string mac, out bool isNew)
    {
        var key = DeviceKey(mac);
        lock (_sync)
        {
            if (_devices.TryGetValue(key, out var settings))
            {
                isNew = false;
                return settings;
            }

            settings = new DeviceScreenSettings(mac, ScreenCatalog.Id(_defaultScreen), DefaultRefreshMinutes, "");
            _devices[key] = settings;
            Save();
            isNew = true;
            return settings;
        }
    }

    /// <summary>Change the settings of a known device and save them. Return the new settings, or null.</summary>
    public DeviceScreenSettings? Update(string key, Func<DeviceScreenSettings, DeviceScreenSettings> change)
    {
        lock (_sync)
        {
            if (!_devices.TryGetValue(key, out var old))
                return null;

            var settings = Normalize(change(old));
            if (settings == old)
                return old;

            _devices[key] = settings;
            Save();
            return settings;
        }
    }

    /// <summary>
    /// Record the screen that the device gets now. Return true if the device got a different screen before.
    /// An unknown last screen (a new device, or after a restart) is not a change.
    /// </summary>
    public bool RecordShown(string mac, ScreenKind screen)
    {
        var key = DeviceKey(mac);
        lock (_sync)
        {
            var changed = _shown.TryGetValue(key, out var previous) && previous != screen;
            _shown[key] = screen;
            return changed;
        }
    }

    /// <summary>The screen of the settings. An unknown id gives the default screen.</summary>
    public ScreenKind ScreenOf(DeviceScreenSettings settings) => ScreenCatalog.Find(settings.Screen) ?? _defaultScreen;

    private static DeviceScreenSettings Normalize(DeviceScreenSettings s) => s with
    {
        RefreshMinutes = Math.Clamp(s.RefreshMinutes, MinRefreshMinutes, MaxRefreshMinutes),
        Note = s.Note.Length > MaxNoteLength ? s.Note[..MaxNoteLength] : s.Note,
    };

    private void Load()
    {
        if (!File.Exists(_file))
            return;

        try
        {
            var devices = JsonSerializer.Deserialize<Dictionary<string, DeviceScreenSettings>>(File.ReadAllText(_file));
            if (devices is not null)
                _devices = devices.ToDictionary(d => d.Key, d => Normalize(d.Value));
            _log.LogInformation("Loaded the screen settings of {Count} devices", _devices.Count);
        }
        catch (Exception ex)
        {
            // Start with the default settings: the device must get an image.
            _log.LogError("Cannot read {File}: {Message}", _file, ex.Message);
        }
    }

    // Write a new file, then replace the old one: a failure must not leave a broken file.
    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_devices, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogError("Cannot write {File}: {Message}", _file, ex.Message);
        }
    }
}
