/// <summary>
/// Reads the forecasts of meteo.gov.ua in the background: at the start, and then when they are old.
/// The informer forecast ("завтра" on the dashboard) is read each hour. The 10-day city forecast is read
/// after 06:00 and 18:00. The screens only read the last forecasts: they do not wait for the site, which is slow.
/// </summary>
public sealed class ForecastRefresher : BackgroundService
{
    // The clients read the site only when a forecast is old. Thus a frequent check costs nothing,
    // and a change of the configuration comes quickly.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly MeteoForecastClient _informer;
    private readonly MeteoCityForecastClient _city;
    private readonly ILogger<ForecastRefresher> _log;

    public ForecastRefresher(MeteoForecastClient informer, MeteoCityForecastClient city, ILogger<ForecastRefresher> log)
    {
        _informer = informer;
        _city = city;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            do
            {
                try
                {
                    await Task.WhenAll(_informer.RefreshIfDueAsync(), _city.RefreshIfDueAsync()).WaitAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The clients log the errors of the site. This is an unexpected error: try again at the next check.
                    _log.LogWarning("Forecast refresh failed: {Message}", ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server stops.
        }
    }
}
