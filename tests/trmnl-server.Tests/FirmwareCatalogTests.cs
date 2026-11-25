using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Trmnl.Server.Services;

namespace Trmnl.Server.Tests;

public class FirmwareCatalogTests
{
    [Fact]
    public void Reads_a_correct_build()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1");

        var builds = FirmwareCatalog.Load(folder.Path, NullLogger.Instance);

        var build = Assert.Single(builds);
        Assert.Equal("1.8.19-own.1", build.Version.ToString());
        Assert.Equal("trmnl-og-1.8.19-own.1.bin", build.FileName);
        Assert.Equal("og", build.Model);
        Assert.Equal(64, build.Sha256.Length);
    }

    [Fact]
    public void Returns_the_newest_version_first()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.9");
        folder.AddBuild("1.8.19-own.10");
        folder.AddBuild("1.8.19");

        var versions = FirmwareCatalog.Load(folder.Path, NullLogger.Instance).Select(b => b.Version.ToString());

        Assert.Equal(["1.8.19-own.10", "1.8.19-own.9", "1.8.19"], versions);
    }

    [Fact]
    public void Missing_folder_has_no_builds()
    {
        Assert.Empty(FirmwareCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), NullLogger.Instance));
    }

    [Fact]
    public void Rejects_a_wrong_sha256()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1", manifestSha256: new string('0', 64));

        Assert.Empty(FirmwareCatalog.Load(folder.Path, NullLogger.Instance));
    }

    [Fact]
    public void Rejects_a_merged_image()
    {
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1", image: FirmwareTestFolder.AppImage("1.8.19-own.1", appDescription: false));

        Assert.Empty(FirmwareCatalog.Load(folder.Path, NullLogger.Instance));
    }

    [Fact]
    public void Rejects_an_image_with_a_different_version_text()
    {
        // The manifest says own.1, but the device would report own.2 after the update: an endless update.
        using var folder = new FirmwareTestFolder();
        folder.AddBuild("1.8.19-own.1", image: FirmwareTestFolder.AppImage("1.8.19-own.2"));

        Assert.Empty(FirmwareCatalog.Load(folder.Path, NullLogger.Instance));
    }

    [Fact]
    public void Rejects_a_file_name_with_a_path()
    {
        using var folder = new FirmwareTestFolder();
        File.WriteAllText(
            Path.Combine(folder.Path, "bad.json"),
            "{\"version\": \"1.8.19-own.1\", \"file\": \"../x.bin\", \"sha256\": \"00\"}");

        Assert.Empty(FirmwareCatalog.Load(folder.Path, NullLogger.Instance));
    }

    [Fact]
    public void Rejects_a_manifest_without_an_image()
    {
        using var folder = new FirmwareTestFolder();
        var file = folder.AddBuild("1.8.19-own.1");
        File.Delete(Path.Combine(folder.Path, file));

        Assert.Empty(FirmwareCatalog.Load(folder.Path, NullLogger.Instance));
    }

    [Theory]
    [InlineData("1.8.19-own.1\0", "1.8.19-own.1", true)]
    [InlineData("FW 1.8.19-own.1 (abc)", "1.8.19-own.1", true)]
    [InlineData("1.8.19-own.12\0", "1.8.19-own.1", false)]
    [InlineData("11.8.19-own.1\0", "1.8.19-own.1", false)]
    [InlineData("1.8.19-own.1\0", "1.8.19", false)]
    [InlineData("x1.8.19-own.12\0 1.8.19-own.1\0", "1.8.19-own.1", true)]
    public void Finds_the_version_text_as_a_whole_value(string content, string version, bool expected)
    {
        Assert.Equal(expected, FirmwareCatalog.ContainsVersionText(Encoding.ASCII.GetBytes(content), version));
    }
}
