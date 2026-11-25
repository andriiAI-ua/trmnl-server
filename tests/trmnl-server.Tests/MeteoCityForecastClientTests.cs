using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Trmnl.Server.Tests;

public sealed class MeteoCityForecastClientTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "trmnl-forecast-tests-" + Guid.NewGuid().ToString("N"));

    private readonly StubHandler _site = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private MeteoCityForecastClient CreateClient(string city = "Київ") =>
        new(new StubFactory(_site),
            new StaticOptions(new DashboardOptions { ForecastCity = city, ForecastRegion = "Київська" }),
            new TestEnvironment(_root),
            NullLogger<MeteoCityForecastClient>.Instance);

    [Fact]
    public async Task RefreshReadsTheSiteOnceAndKeepsTheFile()
    {
        var client = CreateClient();

        await client.RefreshIfDueAsync();
        await client.RefreshIfDueAsync();

        Assert.Equal(1, _site.Calls);
        Assert.True(File.Exists(Path.Combine(_root, "Data", "city-forecast.json")));
    }

    [Fact]
    public async Task NewClientShowsTheForecastFromTheFileWithoutTheSite()
    {
        await CreateClient().RefreshIfDueAsync();

        // A restart: the screen gets the forecast at once, without the site.
        var days = CreateClient().GetDays(DateTime.Today);

        Assert.NotNull(days);
        Assert.Equal(MeteoCityForecastClient.Days, days.Count);
        Assert.Equal(new ForecastPoint(-1, 104), days[0].Night);
        Assert.Equal(new ForecastPoint(3, 3), days[0].Day);
        Assert.Equal(1, _site.Calls);
    }

    [Fact]
    public async Task FileOfAnotherCityIsNotUsed()
    {
        await CreateClient("Київ").RefreshIfDueAsync();

        await CreateClient("Львів").RefreshIfDueAsync();

        Assert.Equal(2, _site.Calls);
    }

    [Fact]
    public async Task DamagedFileIsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Data", "city-forecast.json"), "");

        await CreateClient().RefreshIfDueAsync();

        Assert.Equal(1, _site.Calls);
    }

    // A small answer of fmi.json: the night and the day of today.
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var today = DateTime.Today.ToString("yyyyMMdd");
            var json = "{\"fulldata\":["
                + $"{{\"time\":\"{today}T030000\",\"meantemp\":-1.4,\"SmartSymbol\":104}},"
                + $"{{\"time\":\"{today}T150000\",\"meantemp\":2.6,\"SmartSymbol\":3}}"
                + "]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptions(DashboardOptions value) : IOptionsMonitor<DashboardOptions>
    {
        public DashboardOptions CurrentValue => value;
        public DashboardOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DashboardOptions, string?> listener) => null;
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "trmnl-server";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
