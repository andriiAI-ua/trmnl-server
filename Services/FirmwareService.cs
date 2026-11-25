using System.Collections.Concurrent;

namespace Trmnl.Server.Services;

/// <summary>A build that the server sends to a device.</summary>
/// <param name="Build">The build.</param>
/// <param name="Path">The download path without the leading "/", for example "api/firmware/x.bin?id=AA%3ABB".</param>
public sealed record FirmwareOffer(FirmwareBuild Build, string Path);

/// <summary>
/// Selects the firmware build for a device. The builds are our own builds in the firmware folder (see FirmwareCatalog).
/// Rules:
/// - A device gets the newest build only when the build is newer than the version of the device.
/// - A pinned version ("Version" in the configuration) goes to the device when it is different: this is a rollback.
/// - After MaxDownloads downloads of the same build without the new version, the device does not get this build again.
/// </summary>
public sealed class FirmwareService
{
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<FirmwareService> _log;
    private readonly object _scanLock = new();

    // Downloads of a build (SHA-256) by a device (ID header).
    private readonly ConcurrentDictionary<(string Device, string Sha256), int> _downloads = new();

    // Device and build pairs with a logged download limit, to log the limit one time only.
    private readonly ConcurrentDictionary<(string Device, string Sha256), bool> _limitLogged = new();

    private IReadOnlyList<FirmwareBuild> _builds = [];
    private string? _signature;

    public FirmwareService(IConfiguration cfg, IHostEnvironment env, ILogger<FirmwareService> log)
    {
        _cfg = cfg;
        _env = env;
        _log = log;
    }

    /// <summary>The correct builds, newest version first.</summary>
    public IReadOnlyList<FirmwareBuild> Builds
    {
        get
        {
            if (_signature is null)
                Refresh();

            return _builds;
        }
    }

    public FirmwareOptions ReadOptions() =>
        _cfg.GetSection(FirmwareOptions.Section).Get<FirmwareOptions>() ?? new FirmwareOptions();

    public string ResolveDirectory(FirmwareOptions options) =>
        string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(_env.ContentRootPath, "wwwroot", "firmware")
            : Path.GetFullPath(options.Directory, _env.ContentRootPath);

    /// <summary>Reads the firmware folder again when a file in it changed.</summary>
    public void Refresh()
    {
        lock (_scanLock)
        {
            var directory = ResolveDirectory(ReadOptions());
            var signature = Signature(directory);

            if (signature == _signature)
                return;

            _builds = FirmwareCatalog.Load(directory, _log);
            _signature = signature;

            if (_builds.Count == 0)
            {
                _log.LogInformation("Firmware: no builds in {Directory}", directory);
                return;
            }

            _log.LogInformation(
                "Firmware: {Count} builds in {Directory}: {Builds}",
                _builds.Count,
                directory,
                string.Join(", ", _builds.Select(b => $"{b.Version} ({b.Model ?? "any model"})")));
        }
    }

    /// <summary>Returns the build for the device, or null when the device must not update.</summary>
    /// <param name="deviceId">The "ID" header (MAC address).</param>
    /// <param name="deviceVersion">The "FW-Version" header.</param>
    /// <param name="deviceModel">The "Model" header, for example "og".</param>
    public FirmwareOffer? GetOffer(string? deviceId, string? deviceVersion, string? deviceModel)
    {
        var options = ReadOptions();

        if (options.Update == FirmwareUpdateMode.None)
            return null;

        var rule = string.IsNullOrWhiteSpace(deviceId)
            ? null
            : options.Devices.FirstOrDefault(d => string.Equals(d.Id?.Trim(), deviceId.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!(rule?.Update ?? (options.Update == FirmwareUpdateMode.All)))
            return null;

        if (!FirmwareVersion.TryParse(deviceVersion, out var current))
        {
            _log.LogWarning("Firmware: device {Device} sent an unknown version \"{Version}\". No update", deviceId, deviceVersion);
            return null;
        }

        var builds = Builds.Where(b => ModelMatches(b, deviceModel)).ToList();
        var pinned = FirstNotEmpty(rule?.Version, options.Version);
        FirmwareBuild? target;

        if (pinned is not null)
        {
            if (!FirmwareVersion.TryParse(pinned, out var pinnedVersion))
            {
                _log.LogWarning("Firmware: the pinned version \"{Version}\" is not correct. No update", pinned);
                return null;
            }

            target = builds.FirstOrDefault(b => b.Version == pinnedVersion);

            if (target is null)
            {
                _log.LogWarning(
                    "Firmware: no build {Version} for model {Model} of device {Device}. No update",
                    pinnedVersion, deviceModel, deviceId);
                return null;
            }

            // A pinned build goes to the device also when it is older: this is a rollback.
            if (target.Version == current)
                return null;
        }
        else
        {
            target = builds.FirstOrDefault();

            // Only a newer build. An equal version must not go to the device: the device updates again after each restart.
            if (target is null || target.Version <= current)
                return null;
        }

        if (DownloadLimitReached(deviceId, target, options.MaxDownloads, current))
            return null;

        var path = $"api/firmware/{Uri.EscapeDataString(target.FileName)}";

        if (!string.IsNullOrWhiteSpace(deviceId))
            path += $"?id={Uri.EscapeDataString(deviceId.Trim())}";

        _log.LogInformation(
            "Firmware: offer {Version} to device {Device} ({Model}, now {Current})",
            target.Version, deviceId, deviceModel, current);

        return new FirmwareOffer(target, path);
    }

    /// <summary>Finds a correct build by its file name. The file must not have changed after the scan.</summary>
    public FirmwareBuild? FindBuild(string fileName)
    {
        var build = Builds.FirstOrDefault(b => string.Equals(b.FileName, fileName, StringComparison.Ordinal));

        if (build is null)
            return null;

        var info = new FileInfo(build.FullPath);

        if (!info.Exists || info.Length != build.Size || info.LastWriteTimeUtc != build.LastWriteUtc)
        {
            // The file changed after the scan: scan again before the next download.
            Refresh();
            return null;
        }

        return build;
    }

    /// <summary>Counts a download of the build by the device.</summary>
    public int RecordDownload(string? deviceId, FirmwareBuild build)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return 0;

        return _downloads.AddOrUpdate((Normalize(deviceId), build.Sha256), 1, (_, count) => count + 1);
    }

    private bool DownloadLimitReached(string? deviceId, FirmwareBuild target, int maxDownloads, FirmwareVersion current)
    {
        if (maxDownloads <= 0 || string.IsNullOrWhiteSpace(deviceId))
            return false;

        var key = (Normalize(deviceId), target.Sha256);

        if (_downloads.GetValueOrDefault(key) < maxDownloads)
            return false;

        if (_limitLogged.TryAdd(key, true))
        {
            _log.LogError(
                "Firmware: device {Device} downloaded {Version} {Count} times and still reports {Current}. " +
                "The server stops the update of this build until a restart",
                deviceId, target.Version, _downloads.GetValueOrDefault(key), current);
        }

        return true;
    }

    private static bool ModelMatches(FirmwareBuild build, string? deviceModel) =>
        build.Model is null
        || string.IsNullOrWhiteSpace(deviceModel)
        || string.Equals(build.Model, deviceModel.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Normalize(string deviceId) => deviceId.Trim().ToUpperInvariant();

    // Names, sizes and times of the files. A different value means a changed folder.
    private static string Signature(string directory)
    {
        if (!Directory.Exists(directory))
            return "missing:" + directory;

        var files = new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => f.Extension is ".json" or ".bin")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");

        return directory + "\n" + string.Join("\n", files);
    }
}
