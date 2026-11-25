namespace Trmnl.Server.Tests;

public sealed class ScreenCatalogTests
{
    // No forecast city and no room entities: no "5 квадратів", "10 днів" and "Кімната".
    private static readonly DashboardOptions Plain = new();

    private static readonly DashboardOptions Full = new()
    {
        ForecastCity = "Київ",
        RoomTemperature = ["sensor.room_temperature"],
    };

    [Theory]
    [InlineData(ScreenKind.Dashboard, ScreenKind.Quad)]
    [InlineData(ScreenKind.Quad, ScreenKind.Clock)]
    [InlineData(ScreenKind.Clock, ScreenKind.DaysInYear)]
    [InlineData(ScreenKind.DaysInYear, ScreenKind.Dashboard)]
    public void NextSkipsScreensThatAreNotAvailable(ScreenKind current, ScreenKind expected)
    {
        Assert.Equal(expected, ScreenCatalog.Next(current, Plain, hasNote: false));
    }

    [Theory]
    [InlineData(ScreenKind.Quad, ScreenKind.Quad5)]
    [InlineData(ScreenKind.Quad5, ScreenKind.Days10)]
    [InlineData(ScreenKind.Days10, ScreenKind.Note)]
    [InlineData(ScreenKind.Note, ScreenKind.Room)]
    [InlineData(ScreenKind.Room, ScreenKind.Clock)]
    public void NextFollowsTheListWhenAllScreensAreAvailable(ScreenKind current, ScreenKind expected)
    {
        Assert.Equal(expected, ScreenCatalog.Next(current, Full, hasNote: true));
    }

    [Fact]
    public void NextSkipsTheNoteWithoutText()
    {
        Assert.Equal(ScreenKind.Room, ScreenCatalog.Next(ScreenKind.Days10, Full, hasNote: false));
    }

    [Fact]
    public void NextLeavesAScreenThatIsNotInTheCycle()
    {
        // The note screen without a text, or a screen that is not available now.
        Assert.Equal(ScreenKind.Clock, ScreenCatalog.Next(ScreenKind.Note, Plain, hasNote: false));
        Assert.Equal(ScreenKind.Clock, ScreenCatalog.Next(ScreenKind.Room, Plain, hasNote: false));
    }

    [Fact]
    public void NextNeverReturnsTheSameScreenWhenThereAreOthers()
    {
        foreach (var kind in Enum.GetValues<ScreenKind>())
            Assert.NotEqual(kind, ScreenCatalog.Next(kind, Full, hasNote: true));
    }
}
