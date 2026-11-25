using Microsoft.AspNetCore.HttpOverrides;
using Polly;
using Serilog;
using System.Globalization;
using Trmnl.Server.Services;

CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("uk-UA");
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("uk-UA");

var builder = WebApplication.CreateBuilder(args);

// Keep secrets (for example, the Home Assistant token) in the shared Data folder, not in the release.
// Add the environment variables again, so that they override this file.
builder.Configuration
    .AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "Data", "appsettings.local.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddHttpContextAccessor();

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .Enrich.FromLogContext()
        .Enrich.With(new TraceIdentifierEnricher(
            services.GetRequiredService<IHttpContextAccessor>()))
        .WriteTo.Console(
            outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{CorrelationId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                );
});

builder.Services.AddControllers();
builder.Services.AddRouting();

builder.Services.Configure<HaMqttOptions>(
    builder.Configuration.GetSection(HaMqttOptions.Section));

// The TRMNL devices in Home Assistant: the screen, the refresh interval and the text of each device.
builder.Services.AddSingleton<DeviceScreenStore>();
builder.Services.AddSingleton<HaControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HaControlService>());

builder.Services.AddSingleton<JournalService>();
builder.Services.AddSingleton<JsonResourceProvider>();
builder.Services.AddSingleton<MarkdownResourceProvider>();
builder.Services.AddSingleton<TemplateProcessor>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<ImageCache>();
builder.Services.AddSingleton<ImageRenderer>();
builder.Services.AddSingleton<HumanizerPluralProvider>();

builder.Services.Configure<DisplayOptions>(
    builder.Configuration.GetSection(DisplayOptions.Section));
builder.Services.Configure<DashboardOptions>(
    builder.Configuration.GetSection(DashboardOptions.Section));
builder.Services.Configure<HomeAssistantOptions>(
    builder.Configuration.GetSection(HomeAssistantOptions.Section));
// Home Assistant is in the LAN and the device waits for the screen, so keep the times short.
// The retry handles a short failure. The circuit breaker stops the calls when Home Assistant is down,
// so the screen shows "н/д" at once and does not wait for the timeouts.
builder.Services.AddHttpClient(HomeAssistantClient.HttpClientName)
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(300);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(6);
        // Open the circuit for 30 s when half of at least 5 calls in 30 s fail.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // >= 2 x AttemptTimeout
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });
// The history API can need some seconds. The value of 24 hours ago is read in the background
// and kept for some minutes, so these longer times do not delay the screen.
builder.Services.AddHttpClient(HomeAssistantClient.HistoryHttpClientName)
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(35);
        // Open the circuit for 5 min when half of at least 4 calls in 2 min fail.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2); // >= 2 x AttemptTimeout
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 4;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(5);
    });
builder.Services.AddSingleton<HomeAssistantClient>();
builder.Services.AddSingleton<DashboardDataService>();
builder.Services.AddSingleton<DashboardRenderer>();
builder.Services.AddSingleton<DaysInYearRenderer>();
builder.Services.AddSingleton<ScreenService>();
builder.Services.AddSingleton<PowerStatusStore>();

// ForecastRefresher reads the forecasts in the background, so a longer retry does not delay the screen.
builder.Services.AddHttpClient(MeteoForecastClient.HttpClientName, client =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("trmnl-server"))
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(2);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        // Open the circuit for 15 min when half of at least 3 calls in 5 min fail.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5); // >= 2 x AttemptTimeout
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.MinimumThroughput = 3;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(15);
    });
builder.Services.AddSingleton<MeteoForecastClient>();
builder.Services.AddSingleton<MeteoCityForecastClient>();
builder.Services.AddHostedService<ForecastRefresher>();

builder.Services.AddHealthChecks();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<FirmwareService>();
builder.Services.AddHostedService<FirmwareUpdaterService>();


var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();

app.Use(async (ctx, next) =>
{
    await next();

    if (ctx.Response.StatusCode >= 500)
    {
        app.Logger.LogError(
            "HTTP {Status} {Method} {Path}",
            ctx.Response.StatusCode,
            ctx.Request.Method,
            ctx.Request.Path);
    }
});


app.UseForwardedHeaders(new ForwardedHeadersOptions {
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost
});

app.UseStaticFiles();

app.MapControllers();
app.MapHealthChecks("/health");

Directory.CreateDirectory("wwwroot/screens");

app.Run();
