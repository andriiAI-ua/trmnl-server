using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trmnl.Server.Services;

/// <summary>A checked firmware build from the firmware folder.</summary>
public sealed record FirmwareBuild(
    FirmwareVersion Version,
    string FileName,
    string FullPath,
    string Sha256,
    long Size,
    DateTime LastWriteUtc,
    string? Model,
    string? Commit);

/// <summary>
/// Reads the builds from the firmware folder. Each build has an OTA image "*.bin" and a manifest "*.json":
/// {"version": "1.8.19-own.1", "file": "trmnl-og-1.8.19-own.1.bin", "sha256": "...", "size": 1384656, "model": "og"}.
/// build.sh of the firmware repository writes the manifest.
/// </summary>
public static class FirmwareCatalog
{
    // An ESP image starts with this byte.
    private const byte ImageMagic = 0xE9;

    // An app image has the app description (ESP_APP_DESC_MAGIC_WORD 0xABCD5432, little endian) at offset 0x20.
    // A merged image starts with the bootloader and does not have it.
    private const int AppDescOffset = 0x20;
    private static readonly byte[] AppDescMagic = [0x32, 0x54, 0xCD, 0xAB];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Returns the correct builds, newest version first. Logs a warning for each incorrect build.</summary>
    public static IReadOnlyList<FirmwareBuild> Load(string directory, ILogger log)
    {
        if (!Directory.Exists(directory))
            return [];

        var builds = new List<FirmwareBuild>();

        foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var manifestName = Path.GetFileName(manifestPath);

            try
            {
                var build = Read(manifestPath, out var problem);

                if (build is null)
                    log.LogWarning("Firmware: skip {Manifest}: {Problem}", manifestName, problem);
                else
                    builds.Add(build);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log.LogWarning("Firmware: skip {Manifest}: {Error}", manifestName, ex.Message);
            }
        }

        var result = new List<FirmwareBuild>();

        // Two builds with the same version and model are not correct. Use the newer file.
        foreach (var group in builds.GroupBy(b => (b.Version, Model: b.Model?.ToLowerInvariant())))
        {
            var newest = group.MaxBy(b => b.LastWriteUtc)!;

            if (group.Count() > 1)
            {
                log.LogWarning(
                    "Firmware: {Count} builds have version {Version}, model {Model}. The server uses {File}",
                    group.Count(), group.Key.Version, group.Key.Model, newest.FileName);
            }

            result.Add(newest);
        }

        return result
            .OrderByDescending(b => b.Version)
            .ThenBy(b => b.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads and checks one build. Returns null and the problem when the build is not correct.</summary>
    public static FirmwareBuild? Read(string manifestPath, out string problem)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions);

        if (manifest is null)
            return Fail("the manifest is empty", out problem);

        if (!FirmwareVersion.TryParse(manifest.Version, out var version))
            return Fail($"incorrect version \"{manifest.Version}\"", out problem);

        var fileName = manifest.File?.Trim();

        // Only a file in the same folder: no path, no "..".
        if (string.IsNullOrEmpty(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.StartsWith('.')
            || fileName.Contains('\\')
            || !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"incorrect file name \"{manifest.File}\"", out problem);
        }

        var info = new FileInfo(Path.Combine(Path.GetDirectoryName(manifestPath)!, fileName));

        if (!info.Exists)
            return Fail($"{fileName} is missing", out problem);

        if (manifest.Size is long size && size != info.Length)
            return Fail($"{fileName} has {info.Length} bytes, the manifest has {size}", out problem);

        var data = File.ReadAllBytes(info.FullName);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));

        if (!string.Equals(sha256, manifest.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail($"the SHA-256 of {fileName} is {sha256}, the manifest has {manifest.Sha256}", out problem);

        if (!IsAppImage(data))
            return Fail($"{fileName} is not an ESP app image (a merged image for address 0x0?)", out problem);

        // The device sends this text in the "FW-Version" header after the update.
        // If the image does not contain it, the device asks for the update again and again.
        if (!ContainsVersionText(data, version.ToString()))
            return Fail($"{fileName} does not contain the version text \"{version}\"", out problem);

        problem = "";

        return new FirmwareBuild(
            version,
            fileName,
            info.FullName,
            sha256,
            info.Length,
            info.LastWriteTimeUtc,
            string.IsNullOrWhiteSpace(manifest.Model) ? null : manifest.Model.Trim(),
            string.IsNullOrWhiteSpace(manifest.Commit) ? null : manifest.Commit.Trim());
    }

    public static bool IsAppImage(ReadOnlySpan<byte> data) =>
        data.Length > AppDescOffset + AppDescMagic.Length
        && data[0] == ImageMagic
        && data.Slice(AppDescOffset, AppDescMagic.Length).SequenceEqual(AppDescMagic);

    /// <summary>
    /// Finds the version text as a whole value: "1.8.19-own.1" must not match in "1.8.19-own.12" or "11.8.19-own.1".
    /// </summary>
    public static bool ContainsVersionText(ReadOnlySpan<byte> data, string version)
    {
        var text = Encoding.ASCII.GetBytes(version);
        var start = 0;

        while (start < data.Length)
        {
            var index = data[start..].IndexOf(text);

            if (index < 0)
                return false;

            index += start;

            var before = index > 0 ? data[index - 1] : (byte)0;
            var end = index + text.Length;
            var after = end < data.Length ? data[end] : (byte)0;

            if (!IsVersionChar(before) && !IsVersionChar(after) && after != (byte)'-')
                return true;

            start = index + 1;
        }

        return false;
    }

    private static bool IsVersionChar(byte b) => b is (>= (byte)'0' and <= (byte)'9') or (byte)'.';

    private static FirmwareBuild? Fail(string text, out string problem)
    {
        problem = text;
        return null;
    }

    private sealed class Manifest
    {
        public string? Version { get; set; }
        public string? File { get; set; }
        public string? Sha256 { get; set; }
        public long? Size { get; set; }
        public string? Model { get; set; }
        public string? Commit { get; set; }
    }
}
