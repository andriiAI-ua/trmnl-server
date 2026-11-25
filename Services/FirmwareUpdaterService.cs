namespace Trmnl.Server.Services;

/// <summary>
/// Scans the firmware folder at the start and then at an interval (Firmware:ScanIntervalSeconds).
/// The server uses only our builds in this folder. It does not download builds from GitHub.
/// </summary>
public sealed class FirmwareUpdaterService : BackgroundService
{
    private readonly FirmwareService _firmware;
    private readonly ILogger<FirmwareUpdaterService> _log;

    public FirmwareUpdaterService(FirmwareService firmware, ILogger<FirmwareUpdaterService> log)
    {
        _firmware = firmware;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(5, _firmware.ReadOptions().ScanIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        do
        {
            try
            {
                _firmware.Refresh();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Firmware: the scan of the firmware folder failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
