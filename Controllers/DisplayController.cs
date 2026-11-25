using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Trmnl.Server.Models;
using Trmnl.Server.Services;

[ApiController]
[Route("api/display")]
public class DisplayController : ControllerBase
{
    private readonly DeviceRegistry _registry;
    private readonly ScreenService _screens;
    private readonly FirmwareService _firmware;
    private readonly DeviceScreenStore _settings;
    private readonly HaControlService _control;
    private readonly IOptionsMonitor<DashboardOptions> _dashboard;
    private readonly ILogger<DisplayController> _log;
    private readonly JournalService _journalService;

    public DisplayController(
        DeviceRegistry registry,
        ScreenService screens,
        FirmwareService firmware,
        DeviceScreenStore settings,
        HaControlService control,
        IOptionsMonitor<DashboardOptions> dashboard,
        ILogger<DisplayController> log,
        JournalService journalService)
    {
        _registry = registry;
        _screens = screens;
        _firmware = firmware;
        _settings = settings;
        _control = control;
        _dashboard = dashboard;
        _log = log;
        _journalService = journalService;
    }

    [HttpGet]
    public async Task<IActionResult> Display([FromHeader] TrmnlRequestHeaders headers, CancellationToken cancellationToken)
    {
        _log.LogInformation("Request: {@Headers}", headers);
        if (string.IsNullOrWhiteSpace(headers.AccessToken))
            return Unauthorized();

        var device = await _registry.GetByMac(headers.AccessToken);

        if (device is null)
        {
            return StatusCode(
                403,
                new { error = $"Device with Access-Token {headers.AccessToken} not found" }
            );
        }

        // The screen, the refresh interval and the text come from Home Assistant (see HaControlService).
        DeviceScreenSettings? settings = null;
        var isNew = false;
        if (!string.IsNullOrWhiteSpace(headers.DeviceId))
            settings = _settings.GetOrAdd(headers.DeviceId, out isNew);

        // A short press of the button asks for the next screen, as KEY on the RLCD device.
        // Save the new screen: Home Assistant gets it below with the other states (OnDisplay).
        var buttonNext = settings is not null && string.Equals(headers.Button, "next", StringComparison.OrdinalIgnoreCase);
        if (buttonNext)
        {
            var current = _settings.ScreenOf(settings!);
            var next = ScreenCatalog.Next(current, _dashboard.CurrentValue, !string.IsNullOrWhiteSpace(settings!.Note));
            settings = _settings.Update(DeviceScreenStore.DeviceKey(headers.DeviceId), s => s with { Screen = ScreenCatalog.Id(next) })
                ?? settings;
            _log.LogInformation("Device {Device}: button, the screen changes from {Old} to {New}", headers.DeviceId, current, next);
        }

        var screen = settings is null ? ScreenKind.Dashboard : _settings.ScreenOf(settings);
        var minutes = settings?.RefreshMinutes ?? DeviceScreenStore.DefaultRefreshMinutes;

        // The device reads only update_firmware and firmware_url. It does not read firmware_sha256.
        var firmware = _firmware.GetOffer(headers.DeviceId, headers.FirmwareVersion, Request.Headers["Model"].FirstOrDefault());
        var needsUpdate = firmware is not null;

        var fwUrl = needsUpdate ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/{firmware!.Path}" : null;

        var key = await _screens.RenderAsync(
            screen, settings?.Note ?? "", headers.BatteryVoltage, headers.Width, headers.Height, cancellationToken);

        // A partial refresh keeps a ghost of the old screen. After a change of the screen, ask for a full refresh.
        // The device reads "maximum_compatibility" and then uses REFRESH_FULL (choose_refresh_mode in display.cpp).
        // A press of the button always gets a full refresh: also after a restart, when the last screen is not known.
        var screenChanged = (settings is not null && _settings.RecordShown(headers.DeviceId!, screen)) || buttonNext;
        if (screenChanged)
            _log.LogInformation("Device {Device}: the screen changed to {Screen}, full refresh", headers.DeviceId, screen);

        // MQTT is optional. The call does not wait: the broker can be down.
        if (settings is not null)
        {
            var battery = DashboardDataService.ToBattery(headers.BatteryVoltage, _dashboard.CurrentValue);
            _control.OnDisplay(headers, settings, isNew, battery?.Percent);
        }

        var fileName = $"{Uri.EscapeDataString(key)}.png";
        var imgUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/screens/{fileName}";

        var alreadySent = _journalService.AlreadyRecorded(headers.DeviceId, key);

        var result = new
        {
            // The device draws the screen only when status is 0.
            status = 0,
            image_url = imgUrl,
            filename = fileName,
            refresh_rate = screen == ScreenKind.Clock ? SecondsToNextStep(DateTime.Now, minutes) : minutes * 60,
            update_firmware = needsUpdate,
            firmware_url = fwUrl,
            firmware_sha256 = firmware?.Build.Sha256,
            reset_firmware = false,
            maximum_compatibility = screenChanged
        };

        _journalService.LogRequest(headers.DeviceId, key);
        _log.LogInformation("Display result: {@Result}, already sent: {AlreadySent}", result, alreadySent);
        // Always answer HTTP 200. The device reads the "status" field from the body.
        return Ok(result);
    }

    /// <summary>
    /// Return the seconds to a time just after the next step of the clock (for example 14:35:00 for a step
    /// of 5 minutes), so that the clock shows the time of the step. A wait shorter than 1 minute gets one more
    /// step: a device that wakes some seconds early does not ask two times.
    /// </summary>
    public static int SecondsToNextStep(DateTime now, int stepMinutes)
    {
        const int delay = 3;
        var step = Math.Max(1, stepMinutes) * 60;
        var seconds = (int)now.TimeOfDay.TotalSeconds;
        var wait = step - seconds % step + delay;
        return wait < 60 ? wait + step : wait;
    }
}
