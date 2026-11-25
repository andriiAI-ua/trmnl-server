using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Trmnl.Server.Tests;

public sealed class MeteoForecastClientTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "trmnl-informer-tests-" + Guid.NewGuid().ToString("N"));

    private readonly StubHandler _site = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private MeteoForecastClient CreateClient(string station = "33345") =>
        new(new StubFactory(_site),
            new StaticOptions(new DashboardOptions { ForecastStation = station }),
            new TestEnvironment(_root),
            NullLogger<MeteoForecastClient>.Instance);

    [Fact]
    public void NoForecastBeforeTheFirstRead()
    {
        Assert.Null(CreateClient().GetForecast(DateTime.Today.AddDays(1)));
        Assert.Equal(0, _site.Calls);
    }

    [Fact]
    public async Task RefreshReadsTheSiteOnceAnHour()
    {
        var client = CreateClient();

        await client.RefreshIfDueAsync();
        await client.RefreshIfDueAsync();

        Assert.Equal(1, _site.Calls);
        Assert.Equal(new ForecastDay(DateTime.Today.AddDays(1), 3, "хмарно"), client.GetForecast(DateTime.Today.AddDays(1)));
    }

    [Fact]
    public async Task NewClientUsesTheFileAndDoesNotReadTheSiteAgain()
    {
        await CreateClient().RefreshIfDueAsync();

        // A restart: the forecast is there at once, and the next read comes after 1 hour.
        var client = CreateClient();
        await client.RefreshIfDueAsync();

        Assert.Equal(3, client.GetForecast(DateTime.Today.AddDays(1))?.Temperature);
        Assert.Equal(1, _site.Calls);
    }

    [Fact]
    public async Task FileOfAnotherStationIsNotUsed()
    {
        await CreateClient("33345").RefreshIfDueAsync();

        var client = CreateClient("33658");
        Assert.Null(client.GetForecast(DateTime.Today.AddDays(1)));

        await client.RefreshIfDueAsync();
        Assert.Equal(2, _site.Calls);
    }

    // A small informer script: the forecast for tomorrow.
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var tomorrow = DateTime.Today.AddDays(1).ToString("dd.MM");
            var script = $"<meteogovua-informer-pr-d-dt>{tomorrow}</meteogovua-informer-pr-d-dt>"
                + "<meteogovua-informer-pr-d-icon title=\"хмарно\"></meteogovua-informer-pr-d-icon>"
                + "<meteogovua-informer-pr-d-t>+3°</meteogovua-informer-pr-d-t>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script, Encoding.UTF8, "text/javascript"),
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
