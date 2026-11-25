using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Trmnl.Server.Tests;

public sealed class DeviceScreenStoreTests : IDisposable
{
    private const string Mac = "AA:BB:CC:DD:EE:01";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "trmnl-screen-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DeviceScreenStore CreateStore() =>
        new(new TestEnvironment(_root), Options.Create(new DisplayOptions()), NullLogger<DeviceScreenStore>.Instance);

    [Fact]
    public void FirstScreenIsNotAChange()
    {
        var store = CreateStore();

        Assert.False(store.RecordShown(Mac, ScreenKind.Dashboard));
    }

    [Fact]
    public void SameScreenIsNotAChange()
    {
        var store = CreateStore();
        store.RecordShown(Mac, ScreenKind.Clock);

        Assert.False(store.RecordShown(Mac, ScreenKind.Clock));
    }

    [Fact]
    public void DifferentScreenIsAChangeOnce()
    {
        var store = CreateStore();
        store.RecordShown(Mac, ScreenKind.Dashboard);

        Assert.True(store.RecordShown(Mac, ScreenKind.Clock));
        Assert.False(store.RecordShown(Mac, ScreenKind.Clock));
    }

    [Fact]
    public void DevicesAreSeparate()
    {
        var store = CreateStore();
        store.RecordShown(Mac, ScreenKind.Dashboard);

        Assert.False(store.RecordShown("AA:BB:CC:DD:EE:02", ScreenKind.Clock));
    }

    [Fact]
    public void MacFormatsAreTheSameDevice()
    {
        var store = CreateStore();
        store.RecordShown(Mac, ScreenKind.Dashboard);

        Assert.True(store.RecordShown("AA-BB-CC-DD-EE-01", ScreenKind.Clock));
    }

    [Fact]
    public void NewStoreForgetsTheLastScreen()
    {
        CreateStore().RecordShown(Mac, ScreenKind.Dashboard);

        Assert.False(CreateStore().RecordShown(Mac, ScreenKind.Clock));
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "trmnl-server";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
