using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Trmnl.Server.Services;

namespace Trmnl.Server.Tests;

/// <summary>A temporary firmware folder with fake builds.</summary>
public sealed class FirmwareTestFolder : IDisposable
{
    public FirmwareTestFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trmnl-fw-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Makes a fake app image: the ESP image magic, the app description magic and the version text.</summary>
    public static byte[] AppImage(string versionText, bool appDescription = true)
    {
        var data = new byte[4096];
        data[0] = 0xE9;

        if (appDescription)
            new byte[] { 0x32, 0x54, 0xCD, 0xAB }.CopyTo(data, 0x20);

        var text = Encoding.ASCII.GetBytes(versionText + "\0");
        text.CopyTo(data, 1000);
        return data;
    }

    /// <summary>Writes an image and its manifest. Returns the file name of the image.</summary>
    public string AddBuild(
        string version,
        string model = "og",
        byte[]? image = null,
        string? manifestSha256 = null,
        string? fileName = null)
    {
        image ??= AppImage(version);
        fileName ??= $"trmnl-{model}-{version}.bin";

        File.WriteAllBytes(System.IO.Path.Combine(Path, System.IO.Path.GetFileName(fileName)), image);

        var manifest = new
        {
            version,
            file = fileName,
            sha256 = manifestSha256 ?? Convert.ToHexStringLower(SHA256.HashData(image)),
            size = image.Length,
            model,
            env = "trmnl",
            commit = "abc1234"
        };

        var manifestName = System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetFileName(fileName)) + ".json";
        File.WriteAllText(System.IO.Path.Combine(Path, manifestName), JsonSerializer.Serialize(manifest));

        return fileName;
    }

    /// <summary>Makes a FirmwareService for this folder with the given settings.</summary>
    public FirmwareService Service(Dictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?> { ["Firmware:Directory"] = Path };

        foreach (var pair in settings ?? [])
            values[pair.Key] = pair.Value;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        return new FirmwareService(cfg, new TestEnvironment(Path), NullLogger<FirmwareService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked temporary folder is not a test failure.
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "trmnl-server.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
