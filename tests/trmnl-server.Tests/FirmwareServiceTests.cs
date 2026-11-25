using Trmnl.Server.Services;

namespace Trmnl.Server.Tests;

public class FirmwareServiceTests
{
    private const string Device = "AA:BB:CC:DD:EE:FF";

    [Fact]
    public void Offers_a_newer_build()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        var offer = folder.Service().GetOffer(Device, "1.7.7", "og");

        Assert.NotNull(offer);
        Assert.Equal("1.8.19-own.1", offer.Build.Version.ToString());
        Assert.Equal("api/firmware/trmnl-og-1.8.19-own.1.bin?id=AA%3ABB%3ACC%3ADD%3AEE%3AFF", offer.Path);
    }

    [Fact]
    public void Offers_the_newest_build()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        folder.AddBuild("1.8.19-own.2");

        Assert.Equal("1.8.19-own.2", folder.Service().GetOffer(Device, "1.8.19-own.1", "og")?.Build.Version.ToString());
    }

    [Theory]
    [InlineData("1.8.19-own.1")]
    [InlineData("v1.8.19-own.1")]
    [InlineData("1.8.19-own.2")]
    [InlineData("1.8.20")]
    public void Does_not_offer_the_same_or_an_older_build(string deviceVersion)
    {
        // Equal versions must not update: after the update the device would get the same build again (a loop).
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        Assert.Null(folder.Service().GetOffer(Device, deviceVersion, "og"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Does_not_offer_to_an_unknown_version(string? deviceVersion)
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        Assert.Null(folder.Service().GetOffer(Device, deviceVersion, "og"));
    }

    [Fact]
    public void Does_not_offer_a_build_of_a_different_model()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1", model: "og");

        var service = folder.Service();

        Assert.Null(service.GetOffer(Device, "1.7.7", "x"));
        // Old firmware can send no model: then the model is not checked.
        Assert.NotNull(service.GetOffer(Device, "1.7.7", null));
    }

    [Fact]
    public void After_the_update_the_device_gets_nothing()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        var service = folder.Service();

        var offer = service.GetOffer(Device, "1.7.7", "og");
        Assert.NotNull(offer);
        service.RecordDownload(Device, offer.Build);

        // After the restart the device reports the version of the build.
        Assert.Null(service.GetOffer(Device, offer.Build.Version.ToString(), "og"));
    }

    [Fact]
    public void Pinned_version_rolls_back_and_then_stops()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        folder.AddBuild("1.8.19-own.2");

        var service = folder.Service(new() { ["Firmware:Version"] = "1.8.19-own.1" });

        Assert.Equal("1.8.19-own.1", service.GetOffer(Device, "1.8.19-own.2", "og")?.Build.Version.ToString());
        Assert.Null(service.GetOffer(Device, "1.8.19-own.1", "og"));
    }

    [Fact]
    public void Device_pin_overrides_the_global_pin()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        folder.AddBuild("1.8.19-own.2");

        var service = folder.Service(new()
        {
            ["Firmware:Version"] = "1.8.19-own.2",
            ["Firmware:Devices:0:Id"] = Device.ToLowerInvariant(),
            ["Firmware:Devices:0:Version"] = "1.8.19-own.1"
        });

        Assert.Equal("1.8.19-own.1", service.GetOffer(Device, "1.8.19-own.2", "og")?.Build.Version.ToString());
        Assert.Equal("1.8.19-own.2", service.GetOffer("11:22:33:44:55:66", "1.8.19-own.1", "og")?.Build.Version.ToString());
    }

    [Fact]
    public void Pinned_version_without_a_build_gives_nothing()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.2");

        var service = folder.Service(new() { ["Firmware:Version"] = "1.8.19-own.1" });

        Assert.Null(service.GetOffer(Device, "1.7.7", "og"));
    }

    [Fact]
    public void Listed_mode_updates_only_listed_devices()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        var service = folder.Service(new()
        {
            ["Firmware:Update"] = "Listed",
            ["Firmware:Devices:0:Id"] = Device,
            ["Firmware:Devices:0:Update"] = "true"
        });

        Assert.NotNull(service.GetOffer(Device, "1.7.7", "og"));
        Assert.Null(service.GetOffer("11:22:33:44:55:66", "1.7.7", "og"));
    }

    [Fact]
    public void Device_can_opt_out()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        var service = folder.Service(new()
        {
            ["Firmware:Devices:0:Id"] = Device,
            ["Firmware:Devices:0:Update"] = "false"
        });

        Assert.Null(service.GetOffer(Device, "1.7.7", "og"));
        Assert.NotNull(service.GetOffer("11:22:33:44:55:66", "1.7.7", "og"));
    }

    [Fact]
    public void None_mode_stops_all_updates()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        var service = folder.Service(new()
        {
            ["Firmware:Update"] = "None",
            ["Firmware:Devices:0:Id"] = Device,
            ["Firmware:Devices:0:Update"] = "true"
        });

        Assert.Null(service.GetOffer(Device, "1.7.7", "og"));
    }

    [Fact]
    public void Download_limit_stops_an_update_loop()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        var service = folder.Service(new() { ["Firmware:MaxDownloads"] = "2" });

        for (var i = 0; i < 2; i++)
        {
            var offer = service.GetOffer(Device, "1.7.7", "og");
            Assert.NotNull(offer);
            service.RecordDownload(Device, offer.Build);
        }

        // The device downloaded the build two times and still reports the old version.
        Assert.Null(service.GetOffer(Device, "1.7.7", "og"));
        // Other devices still get the build.
        Assert.NotNull(service.GetOffer("11:22:33:44:55:66", "1.7.7", "og"));
    }

    [Fact]
    public void Finds_the_build_for_a_download_and_sees_a_changed_file()
    {
        using var folder = new FirmwareTestFolder();
        var file = folder.AddBuild("1.8.19-own.1");
        var service = folder.Service();

        Assert.NotNull(service.FindBuild(file));
        Assert.Null(service.FindBuild("other.bin"));

        // A changed image is not sent until a scan checks it again.
        var path = Path.Combine(folder.Path, file);
        File.WriteAllBytes(path, FirmwareTestFolder.AppImage("1.8.19-own.1").Concat(new byte[] { 1 }).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Null(service.FindBuild(file));
    }

    [Fact]
    public void Sees_a_new_build_after_refresh()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");
        var service = folder.Service();

        Assert.Single(service.Builds);

        folder.AddBuild("1.8.19-own.2");
        service.Refresh();

        Assert.Equal("1.8.19-own.2", service.Builds[0].Version.ToString());
    }
}
