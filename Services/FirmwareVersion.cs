using System.Globalization;
using System.Text.RegularExpressions;

namespace Trmnl.Server.Services;

/// <summary>
/// A firmware version: "1.8.19" (upstream build) or "1.8.19-own.2" (our build).
/// A "v" prefix is allowed: "v1.8.19" is the same version as "1.8.19".
/// Order: major, minor, patch, then the own build number.
/// An own build is newer than the upstream build with the same major, minor and patch.
/// </summary>
public readonly partial record struct FirmwareVersion(int Major, int Minor, int Patch, int? OwnBuild)
    : IComparable<FirmwareVersion>
{
    [GeneratedRegex(@"^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:-own\.(\d{1,6}))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out FirmwareVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Pattern().Match(text.Trim());

        if (!match.Success)
            return false;

        version = new FirmwareVersion(
            Number(match.Groups[1].Value),
            Number(match.Groups[2].Value),
            Number(match.Groups[3].Value),
            match.Groups[4].Success ? Number(match.Groups[4].Value) : null);

        return true;
    }

    public int CompareTo(FirmwareVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        // An upstream build (no own number) is older than any own build.
        if (result == 0) result = (OwnBuild ?? -1).CompareTo(other.OwnBuild ?? -1);
        return result;
    }

    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) >= 0;

    /// <summary>The text that the device sends in the "FW-Version" header.</summary>
    public override string ToString() =>
        OwnBuild is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-own.{OwnBuild}";

    private static int Number(string digits) =>
        int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
}
