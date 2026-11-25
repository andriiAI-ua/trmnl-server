namespace Trmnl.Server.Services;

/// <summary>Which devices get firmware updates.</summary>
public enum FirmwareUpdateMode
{
    /// <summary>All devices, except the devices with "Update": false in the list.</summary>
    All,

    /// <summary>Only the devices with "Update": true in the list.</summary>
    Listed,

    /// <summary>No device. This mode ignores the list.</summary>
    None
}

/// <summary>The "Firmware" configuration section.</summary>
public sealed class FirmwareOptions
{
    public const string Section = "Firmware";

    /// <summary>
    /// The folder with the builds (a "*.bin" image and a "*.json" manifest for each build).
    /// A relative path starts at the content root. Empty: "wwwroot/firmware".
    /// </summary>
    public string? Directory { get; set; }

    public FirmwareUpdateMode Update { get; set; } = FirmwareUpdateMode.All;

    /// <summary>
    /// Send this version to all devices, also when it is older (rollback). Empty: the newest build.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Stop sending a build to a device after this number of downloads when the device does not report the new version.
    /// The counters are in memory: a restart of the server sets them to zero. 0: no limit.
    /// </summary>
    public int MaxDownloads { get; set; } = 3;

    /// <summary>The time between two scans of the folder.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;

    /// <summary>The settings for each device. Put this list in one configuration file only: the files merge lists by index.</summary>
    public List<FirmwareDeviceOptions> Devices { get; set; } = [];
}

/// <summary>The firmware settings for one device.</summary>
public sealed class FirmwareDeviceOptions
{
    /// <summary>The MAC address from the "ID" header, for example "AA:BB:CC:DD:EE:FF".</summary>
    public string Id { get; set; } = "";

    /// <summary>true or false: override "Update" for this device. Empty: use "Update".</summary>
    public bool? Update { get; set; }

    /// <summary>Send this version to the device, also when it is older (rollback). Empty: use "Version".</summary>
    public string? Version { get; set; }
}
