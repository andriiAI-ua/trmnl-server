/// <summary>The screens of a TRMNL device, as on the RLCD device (rlcd-dashboard).</summary>
public enum ScreenKind
{
    Dashboard,
    Quad,       // four quadrants: the date, the outdoor air, the alert, the power
    Quad5,      // the date and the outdoor air at the top, the forecast for 3 days at the bottom
    Days10,     // the date, the weather now and the forecast for 10 days
    Note,       // a text from Home Assistant
    Room,       // the room climate from Home Assistant
    Clock,      // an analog clock: good for an update each 5 minutes
    DaysInYear, // the old Markdown screen: the days of the year
}

/// <summary>
/// The ids of the screens for the settings file and their names for Home Assistant.
/// </summary>
public static class ScreenCatalog
{
    private static readonly (ScreenKind Kind, string Id, string Name)[] Screens =
    [
        (ScreenKind.Dashboard, "dashboard", "Дашборд"),
        (ScreenKind.Quad, "quad", "4 квадрати"),
        (ScreenKind.Quad5, "quad5", "5 квадратів"),
        (ScreenKind.Days10, "days10", "10 днів"),
        (ScreenKind.Note, "note", "Текст"),
        (ScreenKind.Room, "room", "Кімната"),
        (ScreenKind.Clock, "clock", "Годинник"),
        (ScreenKind.DaysInYear, "days", "Дні року"),
    ];

    public static string Id(ScreenKind kind) => Screens.First(s => s.Kind == kind).Id;

    public static string Name(ScreenKind kind) => Screens.First(s => s.Kind == kind).Name;

    /// <summary>Return the screen for an id or a name (any case). Return null for an unknown text.</summary>
    public static ScreenKind? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.Trim();
        foreach (var s in Screens)
        {
            if (string.Equals(s.Id, t, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Name, t, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Kind.ToString(), t, StringComparison.OrdinalIgnoreCase))
            {
                return s.Kind;
            }
        }

        return null;
    }

    /// <summary>
    /// Return the screens that the configuration can show, in the order of the list in Home Assistant.
    /// The forecast screens need the city of the forecast, the room screen needs the room entities.
    /// </summary>
    public static IReadOnlyList<ScreenKind> Available(DashboardOptions options) =>
        Screens.Select(s => s.Kind).Where(k => IsAvailable(k, options)).ToList();

    public static bool IsAvailable(ScreenKind kind, DashboardOptions options) => kind switch
    {
        ScreenKind.Quad5 or ScreenKind.Days10 => !string.IsNullOrWhiteSpace(options.ForecastCity),
        ScreenKind.Room => options.HasRoom,
        _ => true
    };

    /// <summary>
    /// Return the screen after the current screen, as KEY on the RLCD device: the order of the list, and the first
    /// screen after the last screen. Skip the screens that are not available, and the note screen without a text.
    /// </summary>
    public static ScreenKind Next(ScreenKind current, DashboardOptions options, bool hasNote)
    {
        var all = Screens.Select(s => s.Kind).ToList();
        var start = all.IndexOf(current);
        for (var step = 1; step <= all.Count; step++)
        {
            var kind = all[(start + step) % all.Count];
            if (IsAvailable(kind, options) && (kind != ScreenKind.Note || hasNote))
                return kind;
        }

        return current;
    }

    /// <summary>True if the screen shows the 10-day forecast.</summary>
    public static bool NeedsForecastDays(ScreenKind kind) => kind is ScreenKind.Quad5 or ScreenKind.Days10;

    /// <summary>The screen for a new device: the "Display:Layout" setting.</summary>
    public static ScreenKind Default(DisplayOptions options) =>
        string.Equals(options.Layout, DisplayOptions.DaysInYearLayout, StringComparison.OrdinalIgnoreCase)
            ? ScreenKind.DaysInYear
            : ScreenKind.Dashboard;
}
