using Trmnl.Server.Services;

namespace Trmnl.Server.Tests;

public class FirmwareVersionTests
{
    [Theory]
    [InlineData("1.8.19", 1, 8, 19, null)]
    [InlineData("v1.7.7", 1, 7, 7, null)]
    [InlineData(" 1.8.19-own.2 ", 1, 8, 19, 2)]
    [InlineData("V1.8.19-OWN.12", 1, 8, 19, 12)]
    public void Parses_correct_versions(string text, int major, int minor, int patch, int? own)
    {
        Assert.True(FirmwareVersion.TryParse(text, out var version));
        Assert.Equal(new FirmwareVersion(major, minor, patch, own), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.8")]
    [InlineData("1.8.19.1")]
    [InlineData("1.8.19-rc1")]
    [InlineData("1.8.19-own")]
    [InlineData("abc")]
    [InlineData("99999999.1.1")]
    public void Rejects_incorrect_versions(string? text)
    {
        Assert.False(FirmwareVersion.TryParse(text, out _));
    }

    [Fact]
    public void Tag_with_v_is_the_same_version_as_the_header()
    {
        // The old code compared the GitHub tag "v1.7.7" with the header "1.7.7" as text:
        // they were never equal, so the device got the same update after each restart.
        FirmwareVersion.TryParse("v1.7.7", out var tag);
        FirmwareVersion.TryParse("1.7.7", out var header);

        Assert.Equal(tag, header);
        Assert.False(tag > header);
    }

    [Theory]
    [InlineData("1.8.19-own.1", "1.8.19")]
    [InlineData("1.8.19-own.10", "1.8.19-own.9")]
    [InlineData("1.8.20", "1.8.19-own.5")]
    [InlineData("1.8.20-own.6", "1.8.20")]
    [InlineData("1.10.0", "1.9.99")]
    [InlineData("1.8.19-own.0", "1.8.19")]
    public void Orders_versions(string newer, string older)
    {
        FirmwareVersion.TryParse(newer, out var a);
        FirmwareVersion.TryParse(older, out var b);

        Assert.True(a > b);
        Assert.True(b < a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("v1.8.19", "1.8.19")]
    [InlineData("1.8.19-OWN.3", "1.8.19-own.3")]
    public void Writes_the_header_text(string text, string expected)
    {
        FirmwareVersion.TryParse(text, out var version);
        Assert.Equal(expected, version.ToString());
    }
}
