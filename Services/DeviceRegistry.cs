using System.Collections.Concurrent;
using System.Text.Json;
using Trmnl.Server;

public class DeviceRegistry
{
    private readonly ILogger<DeviceRegistry> _log;
    private readonly ConcurrentDictionary<string, Device> _devices = new();
    private readonly SimpleAsyncLazy<string> _file;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DeviceRegistry(IHostEnvironment env, ILogger<DeviceRegistry> log)
    {
        _log = log;
        _file = new SimpleAsyncLazy<string>(async () => await EnsureDeviceRegistryExists(env.ContentRootPath));
    }

    async Task<string> EnsureDeviceRegistryExists(string rootPath)
    {
        var dir = Path.Combine(rootPath, "Data");
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, "devices.json");

        if (File.Exists(file))
        {
            await using var stream = File.OpenRead(file);

            await foreach (var d in JsonSerializer.DeserializeAsyncEnumerable<Device>(stream))
            {
                if (!string.IsNullOrWhiteSpace(d?.Mac))
                {
                    _devices[d.Mac] = d;
                }
            }
            _log.LogInformation("Loaded {Count} devices from registry", _devices.Count);
        }
        else
        {
            _log.LogInformation("Device registry not found, starting with an empty registry");
        }
        return file;
    }

    public async Task<Device?> GetByMac(string mac)
    { 
        await _file; // Ensure the registry is loaded before accessing
        _log.LogInformation($"Looking up device with MAC {mac} in {await _file.Value}");
        return _devices.GetValueOrDefault(mac);
    }

    public async Task<Device> RegisterAsync(string mac)
    {
        var dev = new Device
        {
            Mac = mac,
            ApiKey = Guid.NewGuid().ToString("N"),
            FriendlyId = Random.Shared.Next(100000, 999999).ToString()
        };

        _devices[mac] = dev;

        await SaveAsync();

        return dev;
    }

    private async Task SaveAsync()
    {
        var file = await _file;
        await _lock.WaitAsync();

        try
        {
            await using var stream = File.Create(file);
            await JsonSerializer.SerializeAsync(
                stream,
                _devices.Values,
                new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            _lock.Release();
        }
    }
}