using System.Globalization;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Trmnl.Server.Services;

public enum AlertKind { None, Drone, Missile, DroneAndMissile, Unknown }

// The source of the power in the flat. The color image uses it for the color of the status.
public enum PowerKind { Unknown, Dtek, Generator, Off }

/// <param name="Direction">1: up, -1: down, 0: no change.</param>
public sealed record Trend(int Direction, string Text);

/// <summary>
/// All texts and values that the dashboard shows. Two equal views give the same image,
/// except the update time.
/// </summary>
public sealed record DashboardView(
    bool Grayscale,
    string Date,
    string Weekday,
    string Temperature,
    Trend? TemperatureTrend,
    string Tomorrow,
    string Humidity,
    Trend? HumidityTrend,
    string AirQuality,
    string AirQualityNote,
    Trend? AirQualityTrend,
    // 0: clean, 1: acceptable, 2 and more: bad. Null if AQI is not known.
    int? AqiLevel,
    AlertKind Alert,
    string AlertText,
    // The start of the alert, "HH:mm". Empty without an alert or without the time.
    string AlertSince,
    string PowerQueue,
    PowerKind PowerKind,
    string PowerStatus,
    string PowerDetail,
    string PowerEventLabel,
    string PowerEventValue,
    string Voltage,
    int Year,
    double YearProgress,
    string YearPercent,
    string PassedLabel,
    string DaysPassed,
    string LeftLabel,
    string DaysLeft,
    // Empty if the screen has no change: then the footer does not explain the triangles.
    string TrendLegend,
    int? BatteryPercent,
    bool BatteryLow)
{
    public string Fingerprint() => string.Join("|",
        "dashboard-v1", Grayscale, Date, Weekday, Temperature, TemperatureTrend, Tomorrow, Humidity, HumidityTrend,
        AirQuality, AirQualityNote, Alert, AlertText, AlertSince, PowerQueue, PowerStatus, PowerDetail, PowerEventLabel,
        PowerEventValue, Voltage, Year, YearPercent, PassedLabel, DaysPassed, LeftLabel, DaysLeft, TrendLegend,
        AirQualityTrend, AqiLevel, PowerKind,
        BatteryPercent, BatteryLow);
}

/// <summary>
/// Draws the landscape dashboard: 800x480 for an e-paper screen,
/// or a compact 400x300 version for a small screen (for example, a 4.2" reflective LCD).
/// </summary>
public sealed partial class DashboardRenderer
{
    public const int Width = 800;
    public const int Height = 480;

    public const int CompactWidth = 400;
    public const int CompactHeight = 300;

    /// <summary>
    /// Return true if the device screen needs the compact dashboard.
    /// A device without the size headers gets the 800x480 dashboard.
    /// </summary>
    public static bool IsCompact(int? width, int? height) =>
        width is > 0 && height is > 0 && Math.Max(width.Value, height.Value) <= CompactWidth;

    private const float Margin = 12;
    private const float Gap = 10;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("uk-UA");

    private readonly FontFamily _family;
    private readonly HumanizerPluralProvider _plurals;

    public DashboardRenderer(IHostEnvironment env, HumanizerPluralProvider plurals)
    {
        // Both files are the "Roboto" family. The style selects the file.
        var fonts = new FontCollection();
        _family = fonts.Add(FontLocator.Find(env, "Roboto-Regular.ttf"));
        fonts.Add(FontLocator.Find(env, "Roboto-Bold.ttf"));
        _plurals = plurals;
    }

    /* ===============================
       View
       =============================== */

    public DashboardView BuildView(DashboardData data, bool grayscale)
    {
        var now = data.Now;
        var daysInYear = DateTime.IsLeapYear(now.Year) ? 366 : 365;
        var passed = now.DayOfYear;
        var left = daysInYear - passed;
        var progress = (double)passed / daysInYear;

        var (alertKind, alertText) = AlertTexts(data.Alert);
        var (powerKind, powerStatus, powerDetail, eventLabel, eventValue) = PowerTexts(data.Power, now);

        // Round the battery level to 5 %: a small change of the voltage must not give a new image.
        int? battery = data.Battery is { } b ? (int)(Math.Round(b.Percent / 5.0) * 5) : null;

        var temperatureTrend = data.Temperature?.DayChange is { } tc ? MakeTrend(Math.Round(tc, 1), "0.0", "°") : null;
        var humidityTrend = data.Humidity?.DayChange is { } hc ? MakeTrend(Math.Round(hc), "0", "") : null;
        var airQualityTrend = data.AirQuality?.DayChange is { } ac ? AirQualityTrendOf(Math.Round(ac)) : null;
        var hasTrend = temperatureTrend is not null || humidityTrend is not null || airQualityTrend is not null;

        return new DashboardView(
            Grayscale: grayscale,
            Date: now.ToString("d MMMM", Culture),
            Weekday: now.ToString("dddd", Culture),
            Temperature: data.Temperature is { } t ? FormatTemperature(t.Value) : "—",
            TemperatureTrend: temperatureTrend,
            Tomorrow: TomorrowText(data.Tomorrow),
            Humidity: data.Humidity is { } h ? $"{Math.Round(h.Value):0} %" : "н/д",
            HumidityTrend: humidityTrend,
            AirQuality: data.AirQuality is { } a ? $"{Math.Round(a.Value):0}" : "н/д",
            AirQualityNote: data.AirQuality is { } aq ? AirQualityLevel(Math.Round(aq.Value)) : "",
            AirQualityTrend: airQualityTrend,
            AqiLevel: data.AirQuality is { } al ? AqiLevelOf(Math.Round(al.Value)) : null,
            Alert: alertKind,
            AlertText: alertText,
            AlertSince: alertKind is not (AlertKind.None or AlertKind.Unknown) && data.Alert?.Since is { } since
                ? since.ToLocalTime().ToString("HH:mm", Culture)
                : "",
            PowerQueue: data.PowerQueue,
            PowerKind: powerKind,
            PowerStatus: powerStatus,
            PowerDetail: powerDetail,
            PowerEventLabel: eventLabel,
            PowerEventValue: eventValue,
            Voltage: data.Power.Voltage is { } v ? $"{Math.Round(v):0} В" : "н/д",
            Year: now.Year,
            YearProgress: progress,
            YearPercent: $"{Math.Floor(progress * 100):0} %",
            // The verb agrees with the number: "Минув 1 день", "Минули 2 дні", "Минуло 5 днів".
            PassedLabel: Capitalize(_plurals.GetForm("passed", passed)),
            DaysPassed: _plurals.Get("day", passed),
            LeftLabel: Capitalize(_plurals.GetForm("left", left)),
            DaysLeft: _plurals.Get("day", left),
            TrendLegend: hasTrend ? "зміна за добу" : "",
            BatteryPercent: battery,
            BatteryLow: data.Battery?.Low ?? false);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Culture) + text[1..];

    private static string FormatTemperature(double value)
    {
        var rounded = Math.Round(value, 1);
        var text = Math.Abs(rounded).ToString("0.0", Culture);
        return rounded switch
        {
            > 0 => $"+{text}°",
            < 0 => $"−{text}°",
            _ => $"{text}°"
        };
    }

    private static string TomorrowText(ForecastDay? day)
    {
        if (day is null)
            return "";

        var parts = new List<string> { "завтра" };
        if (day.Temperature is { } t)
            parts.Add(t switch { > 0 => $"+{t}°", < 0 => $"−{-t}°", _ => "0°" });

        var text = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(day.Description) ? text : $"{text} · {day.Description}";
    }

    private static Trend MakeTrend(double change, string format, string unit)
    {
        if (change == 0)
            return new Trend(0, "без змін за добу");

        var text = Math.Abs(change).ToString(format, Culture);
        return new Trend(Math.Sign(change), $"{text}{unit} за добу");
    }

    // A lower AQI is cleaner air, so the text tells if the air became cleaner or dirtier.
    private static Trend AirQualityTrendOf(double change) => change switch
    {
        < 0 => new Trend(-1, $"{Math.Abs(change).ToString("0", Culture)} · чистіше"),
        > 0 => new Trend(1, $"{change.ToString("0", Culture)} · брудніше"),
        _ => new Trend(0, "без змін")
    };

    private static int AqiLevelOf(double aqi) => aqi switch
    {
        <= 50 => 0,
        <= 100 => 1,
        <= 150 => 2,
        <= 200 => 3,
        <= 300 => 4,
        _ => 5
    };

    private static string AirQualityLevel(double aqi) => aqi switch
    {
        <= 50 => "чисте",
        <= 100 => "прийнятне",
        // Short text: it shares the line with the AQI value.
        <= 150 => "погане",
        <= 200 => "нездорове",
        <= 300 => "дуже нездорове",
        _ => "небезпечне"
    };

    private static (AlertKind, string) AlertTexts(AlertStatus? alert)
    {
        if (alert is null)
            return (AlertKind.Unknown, "Немає даних про тривогу");

        var kind = (alert.Drone, alert.Missile) switch
        {
            (true, true) => AlertKind.DroneAndMissile,
            (false, true) => AlertKind.Missile,
            (true, false) => AlertKind.Drone,
            _ => AlertKind.None
        };

        var text = kind switch
        {
            AlertKind.DroneAndMissile => "Ракетна і дронова тривога",
            AlertKind.Missile => "Ракетна тривога",
            AlertKind.Drone => "Дронова тривога",
            _ => "Тривоги немає"
        };

        if (kind != AlertKind.None && alert.Since is { } since)
            text += $" · з {since.ToLocalTime():HH:mm}";

        return (kind, text);
    }

    private static bool? PowerOnBySchedule(PowerStatus power) => (power.NextOff, power.NextOn) switch
    {
        ({ } off, { } on) => off < on,
        ({ }, null) => true,
        (null, { }) => false,
        _ => null
    };

    // The flat has power from the grid (DTEK) or from the generator. The power webhook tells about DTEK,
    // the UPS tells if the flat has power at all. Together they give the source.
    private static (PowerKind Kind, string Status, string Detail) PowerStatusTexts(PowerStatus power) =>
        (power.DtekOn, power.FlatPowered) switch
        {
            (_, false) => (PowerKind.Off, "Світла немає", "працює ДБЖ"),
            (true, _) => (PowerKind.Dtek, "Світло є", "від ДТЕК"),
            (false, true) => (PowerKind.Generator, "Генератор", "немає світла від ДТЕК"),
            (false, null) => (PowerKind.Off, "Немає світла", "від ДТЕК"),
            (null, true) => (PowerKind.Dtek, "Світло є", ""),
            _ => PowerOnBySchedule(power) switch
            {
                true => (PowerKind.Dtek, "Світло є", "за графіком"),
                false => (PowerKind.Off, "Світла немає", "за графіком"),
                _ => (PowerKind.Unknown, "н/д", "")
            }
        };

    private static (PowerKind Kind, string Status, string Detail, string EventLabel, string EventValue) PowerTexts(
        PowerStatus power, DateTime now)
    {
        var (kind, status, detail) = PowerStatusTexts(power);

        var nextOff = power.NextOff?.ToLocalTime();
        var nextOn = power.NextOn?.ToLocalTime();

        // Svitlo.live gives only one of the two times: the other one is "unknown".
        // With DTEK power show when it goes off; without DTEK power show when it comes back.
        var dtekOn = kind switch
        {
            PowerKind.Dtek => true,
            PowerKind.Generator or PowerKind.Off => false,
            _ => (bool?)null
        };

        var (label, value) = (dtekOn, nextOff, nextOn) switch
        {
            (true, { } off, var back) => (WithDay("Вимкнення", off.DateTime, now), OffWindow(off, back)),
            // The schedule has an outage now, but the power is on: it can go off at any time until then.
            (true, null, { } back) => ("Вимкнення за графіком", Until(back.DateTime, now)),
            (false, _, { } on) => (WithDay("Увімкнення", on.DateTime, now), $"о {on:HH:mm}"),
            // The schedule has power now, but there is no power: an outage out of the schedule.
            (false, { } off, null) => ("За графіком світло є", Until(off.DateTime, now)),
            (null, { } off, var back) when back is null || off < back => (WithDay("Вимкнення", off.DateTime, now), OffWindow(off, back)),
            (null, _, { } on) => (WithDay("Увімкнення", on.DateTime, now), $"о {on:HH:mm}"),
            _ => ("Графік", power.ScheduleKnown ? "без вимкнень" : "н/д")
        };

        return (kind, status, detail, label, value);
    }

    private static string OffWindow(DateTimeOffset off, DateTimeOffset? back) =>
        back is { } b && b > off ? $"{off:HH:mm}–{b:HH:mm}" : $"о {off:HH:mm}";

    // "до 18:30", "до завтра 06:00", "до 14.11 06:00".
    private static string Until(DateTime time, DateTime now) => (time.Date - now.Date).Days switch
    {
        0 => $"до {time:HH:mm}",
        1 => $"до завтра {time:HH:mm}",
        _ => $"до {time.ToString("dd.MM", Culture)} {time:HH:mm}"
    };

    private static Color PowerColor(PowerKind kind, Palette p) => kind switch
    {
        PowerKind.Dtek => p.Good,
        PowerKind.Generator => p.Warn,
        PowerKind.Off => p.Bad,
        _ => p.Ink
    };

    private static Color AqiColor(int? level, Palette p) => level switch
    {
        0 => p.Good,
        1 => p.Warn,
        >= 2 => p.Bad,
        _ => p.Ink
    };

    // For AQI a lower value is better: a falling AQI is good.
    private static Color AqiTrendColor(Trend trend, Palette p) => trend.Direction switch
    {
        < 0 => p.Good,
        > 0 => p.Bad,
        _ => p.CardMuted
    };

    private static string WithDay(string label, DateTime time, DateTime now)
    {
        var days = (time.Date - now.Date).Days;
        return days switch
        {
            0 => $"{label} сьогодні",
            1 => $"{label} завтра",
            _ => $"{label} {time.ToString("dd.MM", Culture)}"
        };
    }

    /* ===============================
       Image
       =============================== */

    // The color image is for a phone: draw it 2 times larger, so that the text is sharp.
    private const int ColorScale = 2;

    /// <param name="compact">Draw the 400x300 version. It is always black and white.</param>
    /// <param name="color">Draw the 800x480 version in color at 2x size, for Telegram.</param>
    public byte[] Render(DashboardView view, DateTime updatedAt, bool compact = false, bool color = false)
    {
        color &= !compact;
        var grayscale = view.Grayscale && !compact && !color;
        var p = color ? Palette.Colored : grayscale ? Palette.Gray : Palette.Mono;
        var scale = color ? ColorScale : 1;

        using var img = compact
            ? new Image<Rgba32>(CompactWidth, CompactHeight)
            : new Image<Rgba32>(Width * scale, Height * scale);
        img.Mutate(ctx =>
        {
            ctx.Fill(p.Paper);
            if (scale != 1)
                ctx.SetDrawingTransform(Matrix3x2.CreateScale(scale));

            if (compact)
            {
                DrawCompact(ctx, view, p, updatedAt);
            }
            else
            {
                DrawHeader(ctx, view, p);
                DrawAlert(ctx, view, p);

                var cardTop = 230f;
                var cardHeight = 214f;
                var cardWidth = (Width - 2 * Margin - 2 * Gap) / 3f;
                DrawAirCard(ctx, view, p, new RectangleF(Margin, cardTop, cardWidth, cardHeight));
                DrawPowerCard(ctx, view, p, new RectangleF(Margin + cardWidth + Gap, cardTop, cardWidth, cardHeight));
                DrawYearCard(ctx, view, p, new RectangleF(Margin + 2 * (cardWidth + Gap), cardTop, cardWidth, cardHeight));

                DrawFooter(ctx, view, p, updatedAt);
            }

            if (!grayscale && !color)
                ctx.BinaryThreshold(0.5f);
        });

        using var ms = new MemoryStream();
        if (color)
        {
            img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.Rgb });
        }
        else if (grayscale)
        {
            // Use the same 4 levels as the device, so the file shows the real result.
            QuantizeToFourGrays(img);
            img.SaveAsPng(ms);
        }
        else
        {
            // A 1-bit image lets the device do a partial refresh without a flash.
            img.SaveAsPng(ms, new PngEncoder
            {
                ColorType = PngColorType.Grayscale,
                BitDepth = PngBitDepth.Bit1
            });
        }

        return ms.ToArray();
    }

    /* ===============================
       Four quadrants (the RLCD device)
       =============================== */

    /// <summary>
    /// Draw the 400x300 screen of four quadrants for the RLCD device, as a 1-bit BMP. The device firmware has
    /// the same layout on its "4 квадрати" screen. The top left quadrant stays white and empty: the device
    /// draws the date, the time and its own icons there each minute. Top right: the outdoor temperature, the
    /// humidity and the air quality. Bottom left: the alert. Bottom right, black: the power and its next change.
    /// </summary>
    public byte[] RenderQuad(DashboardView view)
    {
        const float w = CompactWidth / 2f;
        const float h = CompactHeight / 2f;
        var p = Palette.Mono;

        using var img = new Image<Rgba32>(CompactWidth, CompactHeight);
        img.Mutate(ctx =>
        {
            ctx.Fill(p.Paper);
            DrawQuadAir(ctx, view, p, w + w / 2);
            DrawQuadAlert(ctx, view, p, new RectangleF(0, h, w, h));
            DrawQuadPower(ctx, view, p, new RectangleF(w, h, w, h));
            ctx.BinaryThreshold(0.5f);
        });

        using var ms = new MemoryStream();
        img.SaveAsBmp(ms, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel1 });
        return ms.ToArray();
    }

    // A centered text in a quadrant column: bold, maxSize or smaller, so that it fits the column (184 px).
    private void QuadText(IImageProcessingContext ctx, string text, float maxSize, Color color, float cx, float baseline) =>
        Text(ctx, text, FitBold(text, maxSize, 184), color, cx, baseline, HorizontalAlignment.Center);

    // Top right, white: the outdoor temperature and humidity; the air quality level in words, the AQI number
    // under it.
    private void DrawQuadAir(IImageProcessingContext ctx, DashboardView v, Palette p, float cx)
    {
        // 5 px lower: the device draws its battery, percent and notes at the top of this quadrant.
        const float y = 5;
        Text(ctx, v.Temperature, Bold(56), p.Ink, cx, y + 62, HorizontalAlignment.Center);
        // The same text as on the device: "надворі · вологість 78 %".
        var outdoor = v.Humidity == "н/д" ? "надворі" : $"надворі · вологість {v.Humidity}";
        Text(ctx, outdoor, Regular(14), p.Ink, cx, y + 82, HorizontalAlignment.Center);
        if (v.AqiLevel is not null)
        {
            QuadText(ctx, v.AirQualityNote, 30, p.Ink, cx, y + 116);
            Text(ctx, $"AQI {v.AirQuality}", Regular(17), p.Ink, cx, y + 140, HorizontalAlignment.Center);
        }
        else
        {
            QuadText(ctx, "AQI н/д", 26, p.Ink, cx, y + 116);
        }
    }

    // Bottom left, white: the alert. An active alert has a thick frame and a warning sign.
    private void DrawQuadAlert(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        var cx = box.Left + box.Width / 2;
        var y = box.Top;
        switch (v.Alert)
        {
            case AlertKind.Unknown:
                QuadText(ctx, "Немає даних", 24, p.Ink, cx, y + 70);
                QuadText(ctx, "про тривогу", 24, p.Ink, cx, y + 104);
                return;
            case AlertKind.None:
                QuadText(ctx, "Тривоги", 30, p.Ink, cx, y + 70);
                QuadText(ctx, "немає", 30, p.Ink, cx, y + 108);
                return;
        }

        // A 4 px frame inside the quadrant, 4 px from its edges.
        ctx.Draw(p.Ink, 4, RoundedRect(new RectangleF(box.Left + 6, y + 6, box.Width - 12, box.Height - 12), 8));

        // The warning sign before "Тривога", centered together.
        const float icon = 30, gap = 8;
        var title = Bold(30);
        var textWidth = TextMeasurer.MeasureAdvance("Тривога", new TextOptions(title)).Width;
        var start = cx - (icon + gap + textWidth) / 2;
        WarningSign(ctx, start, y + 23, icon, p.Ink, p.Paper);
        Text(ctx, "Тривога", title, p.Ink, start + icon + gap, y + 50);

        var kind = v.Alert switch
        {
            AlertKind.DroneAndMissile => "ракетна і дронова",
            AlertKind.Missile => "ракетна",
            _ => "дронова"
        };
        QuadText(ctx, kind, 26, p.Ink, cx, y + 86);
        if (!string.IsNullOrEmpty(v.AlertSince))
            Text(ctx, $"з {v.AlertSince}", Bold(30), p.Ink, cx, y + 128, HorizontalAlignment.Center);
    }

    // Bottom right, black: the power source and the next change. Only three states, as on the device:
    // the power from DTEK, the power from the generator, or no power.
    private void DrawQuadPower(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        ctx.Fill(p.Ink, new RectangularPolygon(box));
        var cx = box.Left + box.Width / 2;
        var y = box.Top;
        var status = v.PowerKind switch
        {
            PowerKind.Dtek => "Світло від ДТЕК",
            PowerKind.Generator => "Світло від генератора",
            _ => "Світла немає"
        };
        QuadText(ctx, status, 30, p.Paper, cx, y + 56);

        // Without a known schedule there is no next change: show nothing, not "н/д".
        if (v.PowerEventValue != "н/д")
        {
            Text(ctx, v.PowerEventLabel, Regular(14), p.Paper, cx, y + 102, HorizontalAlignment.Center);
            QuadText(ctx, v.PowerEventValue, 26, p.Paper, cx, y + 134);
        }
    }

    private void DrawHeader(IImageProcessingContext ctx, DashboardView v, Palette p)
    {
        var box = new RectangleF(Margin, Margin, Width - 2 * Margin, 148);
        ctx.Fill(p.Header, RoundedRect(box, 16));

        var left = box.Left + 24;
        var right = box.Right - 24;

        Text(ctx, v.Date, Bold(68), p.Paper, left, box.Top + 74);
        Text(ctx, v.Weekday, Regular(28), p.HeaderMuted, left + 2, box.Top + 122);

        // With a forecast, the right side has 3 lines: temperature, change, forecast for tomorrow.
        var hasForecast = !string.IsNullOrEmpty(v.Tomorrow);
        var font = Regular(hasForecast ? 22 : 24);
        var trendBaseline = box.Top + (hasForecast ? 104 : 122);

        if (hasForecast)
        {
            Text(ctx, v.Temperature, Bold(72), p.Paper, right, box.Top + 74, HorizontalAlignment.Right);
            Text(ctx, v.Tomorrow, font, p.HeaderMuted, right, box.Top + 134, HorizontalAlignment.Right);
        }
        else
        {
            Text(ctx, v.Temperature, Bold(80), p.Paper, right, box.Top + 82, HorizontalAlignment.Right);
        }

        // Show "надворі" before the change, so it is clear that this is the outdoor temperature.
        if (v.TemperatureTrend is { } trend)
        {
            var trendLeft = TrendText(ctx, trend, font, p.HeaderMuted, right, trendBaseline);
            Text(ctx, "надворі ·", font, p.HeaderMuted, trendLeft - 8, trendBaseline, HorizontalAlignment.Right);
        }
        else
        {
            var label = v.Temperature == "—" ? "надворі: н/д" : "надворі";
            Text(ctx, label, font, p.HeaderMuted, right, trendBaseline, HorizontalAlignment.Right);
        }
    }

    private void DrawAlert(IImageProcessingContext ctx, DashboardView v, Palette p) =>
        DrawAlertStrip(ctx, v, p, new RectangleF(Margin, 170, Width - 2 * Margin, 50), radius: 12, textSize: 26, iconSize: 30);

    // Draw the alert strip. The sizes are for a strip of 50 px; a smaller strip uses smaller sizes.
    private void DrawAlertStrip(
        IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box, float radius, float textSize, float iconSize)
    {
        var shape = RoundedRect(box, radius);

        var (fill, text) = v.Alert switch
        {
            AlertKind.Missile or AlertKind.DroneAndMissile => (p.MissileFill, p.MissileText),
            AlertKind.Drone => (p.DroneFill, p.DroneText),
            AlertKind.Unknown => (p.Paper, p.Muted),
            _ => (p.AlertNoneFill, p.AlertNoneText)
        };

        ctx.Fill(fill, shape);
        if (fill == p.Paper)
            ctx.Draw(v.Alert == AlertKind.Unknown ? p.Muted : p.Ink, 2, shape);

        var font = v.Alert is AlertKind.None or AlertKind.Unknown ? Regular(textSize) : Bold(textSize * 1.08f);
        // Put the middle of the capital letters on the middle of the strip.
        var baseline = box.Top + box.Height / 2 + textSize * 0.36f;
        var cx = box.Left + box.Width / 2;

        if (v.Alert is AlertKind.Drone or AlertKind.Missile or AlertKind.DroneAndMissile)
        {
            // Put a warning sign before the text and center them together.
            var textWidth = TextMeasurer.MeasureAdvance(v.AlertText, new TextOptions(font)).Width;
            var iconGap = iconSize * 0.4f;
            var start = cx - (iconSize + iconGap + textWidth) / 2;
            WarningSign(ctx, start, box.Top + (box.Height - iconSize * 0.9f) / 2, iconSize, text, fill);
            Text(ctx, v.AlertText, font, text, start + iconSize + iconGap, baseline);
        }
        else
        {
            Text(ctx, v.AlertText, font, text, cx, baseline, HorizontalAlignment.Center);
        }
    }

    private void DrawAirCard(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        Card(ctx, box, p);
        var left = box.Left + 16;
        var right = box.Right - 16;

        Text(ctx, "Повітря надворі", Bold(22), p.Ink, left, box.Top + 34);

        Text(ctx, "Вологість", Regular(20), p.CardMuted, left, box.Top + 76);
        Text(ctx, v.Humidity, Bold(40), p.Ink, left, box.Top + 120);
        if (v.HumidityTrend is { } trend)
            TrendText(ctx, trend with { Text = TrimDay(trend.Text) }, Regular(20), p.CardMuted, right, box.Top + 120);

        Text(ctx, "Якість (AQI)", Regular(20), p.CardMuted, left, box.Top + 156);
        Text(ctx, v.AirQuality, Bold(40), AqiColor(v.AqiLevel, p), left, box.Top + 200);

        // The right side has the change above the level, for example "▼ 5 · чистіше" and "прийнятне".
        if (v.AirQualityTrend is { } aqiTrend)
            TrendText(ctx, aqiTrend, Regular(18), AqiTrendColor(aqiTrend, p), right, box.Top + 177);
        Text(ctx, v.AirQualityNote, Regular(20), p.CardMuted, right, box.Top + 200, HorizontalAlignment.Right);
    }

    private void DrawPowerCard(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        Card(ctx, box, p);
        var left = box.Left + 16;
        var right = box.Right - 16;

        Bolt(ctx, left, box.Top + 14, 14, 22, p.Ink);
        Text(ctx, "Світло", Bold(22), p.Ink, left + 20, box.Top + 34);
        if (!string.IsNullOrWhiteSpace(v.PowerQueue))
            Text(ctx, v.PowerQueue, Regular(20), p.CardMuted, right, box.Top + 34, HorizontalAlignment.Right);

        Text(ctx, v.PowerStatus, Bold(28), PowerColor(v.PowerKind, p), left, box.Top + 70);
        Text(ctx, v.PowerDetail, Regular(18), p.CardMuted, left, box.Top + 94);

        Text(ctx, v.PowerEventLabel, Regular(18), p.CardMuted, left, box.Top + 124);
        Text(ctx, v.PowerEventValue, FitBold(v.PowerEventValue, 32, right - left), p.Ink, left, box.Top + 160);

        Text(ctx, "Напруга", Regular(20), p.CardMuted, left, box.Top + 198);
        Text(ctx, v.Voltage, Bold(22), p.Ink, right, box.Top + 198, HorizontalAlignment.Right);
    }

    private void DrawYearCard(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        Card(ctx, box, p);
        var left = box.Left + 16;
        var right = box.Right - 16;

        Text(ctx, $"Рік {v.Year}", Bold(22), p.Ink, left, box.Top + 34);
        Text(ctx, v.YearPercent, Bold(22), p.Ink, right, box.Top + 34, HorizontalAlignment.Right);

        // A bar with one cell for each month. The marker shows today.
        var bar = new RectangleF(left, box.Top + 66, right - left, 30);
        var cell = bar.Width / 12;
        var filled = (float)(bar.Width * v.YearProgress);

        ctx.Fill(p.Paper, new RectangularPolygon(bar));
        ctx.Fill(p.Accent, new RectangularPolygon(bar.Left, bar.Top, filled, bar.Height));
        for (var i = 1; i < 12; i++)
        {
            var x = bar.Left + i * cell;
            var color = x <= bar.Left + filled ? p.Paper : p.Accent;
            ctx.Fill(color, new RectangularPolygon(x - 1, bar.Top, 2, bar.Height));
        }
        ctx.Draw(p.Accent, 2, new RectangularPolygon(bar));

        var mx = bar.Left + filled;
        ctx.Fill(p.Accent, new Polygon(new LinearLineSegment(
            new PointF(mx - 8, bar.Top - 14), new PointF(mx + 8, bar.Top - 14), new PointF(mx, bar.Top - 3))));

        Text(ctx, "I", Regular(16), p.CardMuted, bar.Left + cell / 2, bar.Bottom + 20, HorizontalAlignment.Center);
        Text(ctx, "IV", Regular(16), p.CardMuted, bar.Left + cell * 3.5f, bar.Bottom + 20, HorizontalAlignment.Center);
        Text(ctx, "VII", Regular(16), p.CardMuted, bar.Left + cell * 6.5f, bar.Bottom + 20, HorizontalAlignment.Center);
        Text(ctx, "X", Regular(16), p.CardMuted, bar.Left + cell * 9.5f, bar.Bottom + 20, HorizontalAlignment.Center);

        Text(ctx, v.PassedLabel, Regular(19), p.CardMuted, left, box.Top + 160);
        Text(ctx, v.DaysPassed, Bold(24), p.Ink, right, box.Top + 160, HorizontalAlignment.Right);
        Text(ctx, v.LeftLabel, Regular(19), p.CardMuted, left, box.Top + 198);
        Text(ctx, v.DaysLeft, Bold(24), p.Ink, right, box.Top + 198, HorizontalAlignment.Right);
    }

    private void DrawFooter(IImageProcessingContext ctx, DashboardView v, Palette p, DateTime updatedAt)
    {
        var baseline = Height - 12f;
        var font = Regular(18);

        // Explain the triangles with the same triangles: "▲▼ зміна за добу".
        if (!string.IsNullOrEmpty(v.TrendLegend))
        {
            var x = Margin + 4;
            var size = font.Size * 0.5f;
            var half = size / 2;
            var cy = baseline - font.Size * 0.36f;
            ctx.Fill(p.Muted, new Polygon(new LinearLineSegment(
                new PointF(x, cy + half * 0.9f), new PointF(x + size, cy + half * 0.9f), new PointF(x + half, cy - half * 0.9f))));
            x += size + 3;
            ctx.Fill(p.Muted, new Polygon(new LinearLineSegment(
                new PointF(x, cy - half * 0.9f), new PointF(x + size, cy - half * 0.9f), new PointF(x + half, cy + half * 0.9f))));
            Text(ctx, v.TrendLegend, font, p.Muted, x + size + 8, baseline);
        }

        var right = Width - Margin - 4;
        if (v.BatteryPercent is { } percent)
        {
            if (v.BatteryLow)
            {
                // Show a black label: the device needs a charge.
                var label = $"Зарядіть пристрій · {percent} %";
                var bold = Bold(18);
                var w = TextMeasurer.MeasureAdvance(label, new TextOptions(bold)).Width;
                // Pill: padding 12, battery icon 31, gap 10, text, padding 14.
                var pill = new RectangleF(right - w - 67, baseline - 20, w + 67, 27);
                ctx.Fill(p.Bad, RoundedRect(pill, 13));
                Battery(ctx, pill.Left + 12, baseline - 14, percent, p.Paper, p.Bad);
                Text(ctx, label, bold, p.Paper, pill.Right - 14, baseline, HorizontalAlignment.Right);
                right = pill.Left - 14;
            }
            else
            {
                var label = $"{percent} %";
                Text(ctx, label, font, p.Muted, right, baseline, HorizontalAlignment.Right);
                var w = TextMeasurer.MeasureAdvance(label, new TextOptions(font)).Width;
                Battery(ctx, right - w - 38, baseline - 14, percent, p.Muted, p.Paper);
                right -= w + 52;
            }
        }

        Text(ctx, $"оновлено {updatedAt:HH:mm}", font, p.Muted, right, baseline, HorizontalAlignment.Right);
    }

    /* ===============================
       Compact 400x300
       =============================== */

    private const float CompactMargin = 6;

    private void DrawCompact(IImageProcessingContext ctx, DashboardView v, Palette p, DateTime updatedAt)
    {
        const float m = CompactMargin;
        var width = CompactWidth - 2 * m;

        DrawCompactHeader(ctx, v, p, new RectangleF(m, m, width, 94));
        DrawAlertStrip(ctx, v, p, new RectangleF(m, 106, width, 32), radius: 8, textSize: 17, iconSize: 20);

        var cardWidth = (width - m) / 2;
        DrawCompactAirCard(ctx, v, p, new RectangleF(m, 144, cardWidth, 118));
        DrawCompactPowerCard(ctx, v, p, new RectangleF(m + cardWidth + m, 144, cardWidth, 118));

        DrawCompactFooter(ctx, v, p, updatedAt);
    }

    private void DrawCompactHeader(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        ctx.Fill(p.Header, RoundedRect(box, 10));
        var left = box.Left + 10;
        var right = box.Right - 10;

        Text(ctx, v.Date, Bold(30), p.Paper, left, box.Top + 40);
        Text(ctx, v.Weekday, Regular(17), p.HeaderMuted, left + 1, box.Top + 66);

        Text(ctx, v.Temperature, Bold(40), p.Paper, right, box.Top + 44, HorizontalAlignment.Right);

        var font = Regular(14);
        if (v.TemperatureTrend is { } trend)
        {
            var trendLeft = TrendText(ctx, trend, font, p.HeaderMuted, right, box.Top + 66);
            Text(ctx, "надворі ·", font, p.HeaderMuted, trendLeft - 6, box.Top + 66, HorizontalAlignment.Right);
        }
        else
        {
            var label = v.Temperature == "—" ? "надворі: н/д" : "надворі";
            Text(ctx, label, font, p.HeaderMuted, right, box.Top + 66, HorizontalAlignment.Right);
        }

        Text(ctx, v.Tomorrow, font, p.HeaderMuted, right, box.Top + 86, HorizontalAlignment.Right);
    }

    private void DrawCompactAirCard(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        Card(ctx, box, p, radius: 10);
        var left = box.Left + 10;
        var right = box.Right - 10;
        var small = Regular(14);
        var value = Bold(26);

        Text(ctx, "Повітря надворі", Bold(15), p.Ink, left, box.Top + 20);

        Text(ctx, "Вологість", small, p.CardMuted, left, box.Top + 44);
        if (v.HumidityTrend is { } trend)
        {
            var t = trend with { Text = TrimDay(trend.Text) };
            TrendText(ctx, t, small, p.CardMuted, left + TrendWidth(t, small), box.Top + 61);
        }
        Text(ctx, v.Humidity, value, p.Ink, right, box.Top + 58, HorizontalAlignment.Right);

        Text(ctx, "Якість (AQI)", small, p.CardMuted, left, box.Top + 86);
        Text(ctx, v.AirQualityNote, small, p.CardMuted, left, box.Top + 103);
        Text(ctx, v.AirQuality, value, p.Ink, right, box.Top + 100, HorizontalAlignment.Right);

        // Put the change after the level: the full text if it fits, else only the number.
        if (v.AirQualityTrend is { } aqiTrend)
        {
            var noteWidth = TextMeasurer.MeasureAdvance(v.AirQualityNote, new TextOptions(small)).Width;
            var valueLeft = right - TextMeasurer.MeasureAdvance(v.AirQuality, new TextOptions(value)).Width;
            var start = left + noteWidth + (noteWidth > 0 ? 8 : 0);
            var shortTrend = aqiTrend with { Text = aqiTrend.Text.Split(" · ")[0] };
            var fit = new[] { aqiTrend, shortTrend }.FirstOrDefault(t => start + TrendWidth(t, small) <= valueLeft - 6);
            if (fit is not null)
                TrendText(ctx, fit, small, p.CardMuted, start + TrendWidth(fit, small), box.Top + 103);
        }
    }

    private void DrawCompactPowerCard(IImageProcessingContext ctx, DashboardView v, Palette p, RectangleF box)
    {
        Card(ctx, box, p, radius: 10);
        var left = box.Left + 10;
        var right = box.Right - 10;
        var small = Regular(14);

        Bolt(ctx, left, box.Top + 7, 10, 16, p.Ink);
        Text(ctx, "Світло", Bold(15), p.Ink, left + 14, box.Top + 20);
        if (!string.IsNullOrWhiteSpace(v.PowerQueue))
            Text(ctx, v.PowerQueue, Regular(13), p.CardMuted, right, box.Top + 20, HorizontalAlignment.Right);

        Text(ctx, v.PowerStatus, Bold(18), p.Ink, left, box.Top + 41);
        Text(ctx, v.Voltage, small, p.CardMuted, right, box.Top + 41, HorizontalAlignment.Right);
        Text(ctx, v.PowerDetail, Regular(13), p.CardMuted, left, box.Top + 57);

        Text(ctx, v.PowerEventLabel, Regular(13), p.CardMuted, left, box.Top + 78);
        Text(ctx, v.PowerEventValue, FitBold(v.PowerEventValue, 24, right - left), p.Ink, left, box.Top + 105);
    }

    // The width and the height are of the drawing area: the 400x300 screen, or the area of the 800x480 screens
    // that draw the compact layouts at a larger scale.
    private void DrawCompactFooter(
        IImageProcessingContext ctx, DashboardView v, Palette p, DateTime updatedAt,
        float width = CompactWidth, float height = CompactHeight)
    {
        var baseline = height - 12f;
        var font = Regular(14);
        var left = CompactMargin + 4;
        var right = width - CompactMargin - 4;

        // Right side: the battery, then the update time.
        if (v.BatteryPercent is { } percent)
        {
            if (v.BatteryLow)
            {
                var label = $"Зарядіть · {percent} %";
                var bold = Bold(14);
                var w = TextMeasurer.MeasureAdvance(label, new TextOptions(bold)).Width;
                var pill = new RectangleF(right - w - 61, baseline - 16, w + 61, 22);
                ctx.Fill(p.Ink, RoundedRect(pill, 11));
                Battery(ctx, pill.Left + 10, baseline - 12, percent, p.Paper, p.Ink);
                Text(ctx, label, bold, p.Paper, pill.Right - 10, baseline, HorizontalAlignment.Right);
                right = pill.Left - 8;
            }
            else
            {
                var label = $"{percent} %";
                Text(ctx, label, font, p.Muted, right, baseline, HorizontalAlignment.Right);
                var w = TextMeasurer.MeasureAdvance(label, new TextOptions(font)).Width;
                Battery(ctx, right - w - 36, baseline - 12, percent, p.Muted, p.Paper);
                right -= w + 46;
            }
        }

        var time = $"{updatedAt:HH:mm}";
        Text(ctx, time, font, p.Muted, right, baseline, HorizontalAlignment.Right);
        right -= TextMeasurer.MeasureAdvance(time, new TextOptions(font)).Width + 12;

        // Left side: the year progress. Use a shorter text if the full text does not fit.
        Text(ctx, "рік", font, p.Muted, left, baseline);
        var bar = new RectangleF(left + 26, baseline - 10, 50, 10);
        ctx.Fill(p.Paper, new RectangularPolygon(bar));
        ctx.Fill(p.Muted, new RectangularPolygon(bar.Left, bar.Top, (float)(bar.Width * v.YearProgress), bar.Height));
        ctx.Draw(p.Muted, 1.5f, new RectangularPolygon(bar));

        var textLeft = bar.Right + 6;
        var leftLabel = v.LeftLabel.ToLower(Culture);
        var candidates = new[]
        {
            $"{v.YearPercent} · {leftLabel} {v.DaysLeft}",
            $"{v.YearPercent} · {v.DaysLeft}",
            v.YearPercent,
        };
        var yearText = candidates.First(c =>
            c == v.YearPercent || TextMeasurer.MeasureAdvance(c, new TextOptions(font)).Width <= right - textLeft);
        Text(ctx, yearText, font, p.Muted, textLeft, baseline);
    }

    /* ===============================
       Shapes
       =============================== */

    private static void Card(IImageProcessingContext ctx, RectangleF box, Palette p, float radius = 14)
    {
        var shape = RoundedRect(box, radius);
        ctx.Fill(p.Fill, shape);
        if (p.CardBorder)
            ctx.Draw(p.Ink, 2, shape);
    }

    private static IPath RoundedRect(RectangleF box, float radius)
    {
        var r = Math.Min(radius, Math.Min(box.Width, box.Height) / 2);
        var points = new List<PointF>(40);

        void Arc(float cx, float cy, float startDegrees)
        {
            for (var i = 0; i <= 8; i++)
            {
                var a = (startDegrees + 90f * i / 8) * MathF.PI / 180f;
                points.Add(new PointF(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)));
            }
        }

        Arc(box.Right - r, box.Top + r, 270);
        Arc(box.Right - r, box.Bottom - r, 0);
        Arc(box.Left + r, box.Bottom - r, 90);
        Arc(box.Left + r, box.Top + r, 180);

        return new Polygon(new LinearLineSegment(points.ToArray()));
    }

    private void WarningSign(IImageProcessingContext ctx, float x, float y, float size, Color color, Color background)
    {
        var h = size * 0.9f;
        ctx.Fill(color, new Polygon(new LinearLineSegment(
            new PointF(x + size / 2, y), new PointF(x + size, y + h), new PointF(x, y + h))));
        Text(ctx, "!", Bold(size * 0.7f), background, x + size / 2, y + h - 3, HorizontalAlignment.Center);
    }

    private static void Bolt(IImageProcessingContext ctx, float x, float y, float w, float h, Color color)
    {
        ctx.Fill(color, new Polygon(new LinearLineSegment(
            new PointF(x + w * 0.6f, y),
            new PointF(x, y + h * 0.58f),
            new PointF(x + w * 0.45f, y + h * 0.58f),
            new PointF(x + w * 0.35f, y + h),
            new PointF(x + w, y + h * 0.4f),
            new PointF(x + w * 0.55f, y + h * 0.4f))));
    }

    private static void Battery(IImageProcessingContext ctx, float x, float y, int percent, Color color, Color background)
    {
        const float w = 28, h = 14;
        var body = new RectangleF(x, y, w, h);
        ctx.Fill(background, new RectangularPolygon(body));
        ctx.Draw(color, 2, new RectangularPolygon(body));
        ctx.Fill(color, new RectangularPolygon(x + w, y + 4, 3, h - 8));

        var level = Math.Clamp(percent, 0, 100) / 100f * (w - 6);
        if (level > 0)
            ctx.Fill(color, new RectangularPolygon(x + 3, y + 3, level, h - 6));
    }

    /* ===============================
       Text
       =============================== */

    private Font Regular(float size) => _family.CreateFont(size, FontStyle.Regular);
    private Font Bold(float size) => _family.CreateFont(size, FontStyle.Bold);

    // Return a bold font of this size, or a smaller one if the text is wider than maxWidth.
    private Font FitBold(string text, float size, float maxWidth)
    {
        var font = Bold(size);
        var width = TextMeasurer.MeasureAdvance(text, new TextOptions(font)).Width;
        return width <= maxWidth ? font : Bold(size * maxWidth / width);
    }

    // Draw a text line. The y value is the baseline.
    private static void Text(
        IImageProcessingContext ctx,
        string text,
        Font font,
        Color color,
        float x,
        float baseline,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var metrics = font.FontMetrics;
        var ascent = metrics.HorizontalMetrics.Ascender * font.Size / metrics.UnitsPerEm;

        ctx.DrawText(new RichTextOptions(font)
        {
            Origin = new PointF(x, baseline - ascent),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Top,
        }, text, color);
    }

    // Draw a trend text with a triangle before it. The text ends at x. Return the left edge of the drawing.
    private static float TrendText(IImageProcessingContext ctx, Trend trend, Font font, Color color, float right, float baseline)
    {
        Text(ctx, trend.Text, font, color, right, baseline, HorizontalAlignment.Right);
        var width = TextMeasurer.MeasureAdvance(trend.Text, new TextOptions(font)).Width;
        if (trend.Direction == 0)
            return right - width;

        var size = font.Size * 0.5f;
        var cx = right - width - size * 0.5f - 6;
        var cy = baseline - font.Size * 0.36f;
        var half = size / 2;

        var points = trend.Direction > 0
            ? new[] { new PointF(cx - half, cy + half * 0.9f), new PointF(cx + half, cy + half * 0.9f), new PointF(cx, cy - half * 0.9f) }
            : new[] { new PointF(cx - half, cy - half * 0.9f), new PointF(cx + half, cy - half * 0.9f), new PointF(cx, cy + half * 0.9f) };

        ctx.Fill(color, new Polygon(new LinearLineSegment(points)));
        return cx - half;
    }

    // Return the width of a trend text with its triangle.
    private static float TrendWidth(Trend trend, Font font)
    {
        var width = TextMeasurer.MeasureAdvance(trend.Text, new TextOptions(font)).Width;
        return trend.Direction == 0 ? width : width + font.Size * 0.5f + 6;
    }

    // Draw a right-aligned note. If the note is wider than maxWidth, put the last word on a second line.
    private static void RightNote(
        IImageProcessingContext ctx, string text, Font font, Color color, float right, float baseline, float maxWidth)
    {
        var width = TextMeasurer.MeasureAdvance(text, new TextOptions(font)).Width;
        var split = text.LastIndexOf(' ');
        if (width <= maxWidth || split < 0)
        {
            Text(ctx, text, font, color, right, baseline, HorizontalAlignment.Right);
            return;
        }

        Text(ctx, text[..split], font, color, right, baseline - font.Size * 1.15f, HorizontalAlignment.Right);
        Text(ctx, text[(split + 1)..], font, color, right, baseline, HorizontalAlignment.Right);
    }

    private static string TrimDay(string text) => text.Replace(" за добу", "");

    /* ===============================
       Colors
       =============================== */

    // Map each pixel to the 4 gray levels of the device: it uses the 2 high bits of the gray value.
    private static void QuantizeToFourGrays(Image<Rgba32> img)
    {
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var px = ref row[x];
                    var gray = (px.R + px.G * 2 + px.B) / 4;
                    var level = (byte)((gray >> 6) * 85);
                    px = new Rgba32(level, level, level, 255);
                }
            }
        });
    }

    /// <param name="Header">Fill of the header. Its texts use Paper and HeaderMuted.</param>
    /// <param name="Good">Good state: power from DTEK, clean air, cleaner air.</param>
    /// <param name="Warn">Warning state: the generator, acceptable air.</param>
    /// <param name="Bad">Bad state: no power, dirty air, dirtier air, low battery.</param>
    /// <param name="Accent">The year bar and its marker.</param>
    private sealed record Palette(
        Color Ink,
        Color Strong,
        Color Muted,
        Color Fill,
        Color Paper,
        Color HeaderMuted,
        Color CardMuted,
        bool CardBorder,
        Color Header,
        Color AlertNoneFill,
        Color AlertNoneText,
        Color DroneFill,
        Color DroneText,
        Color MissileFill,
        Color MissileText,
        Color Good,
        Color Warn,
        Color Bad,
        Color Accent)
    {
        private static readonly Color Black = Color.FromRgb(0, 0, 0);
        private static readonly Color Dark = Color.FromRgb(0x55, 0x55, 0x55);
        private static readonly Color Light = Color.FromRgb(0xAA, 0xAA, 0xAA);
        private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

        // Black and white only: the cards have a border instead of a gray fill.
        public static readonly Palette Mono = new(
            Ink: Black, Strong: Black, Muted: Black, Fill: White, Paper: White,
            HeaderMuted: White, CardMuted: Black, CardBorder: true,
            Header: Black, AlertNoneFill: White, AlertNoneText: Black, DroneFill: Black, DroneText: White,
            MissileFill: Black, MissileText: White, Good: Black, Warn: Black, Bad: Black, Accent: Black);

        public static readonly Palette Gray = new(
            Ink: Black, Strong: Dark, Muted: Dark, Fill: Light, Paper: White,
            HeaderMuted: Light, CardMuted: Black, CardBorder: false,
            Header: Black, AlertNoneFill: Light, AlertNoneText: Black, DroneFill: Dark, DroneText: White,
            MissileFill: Black, MissileText: White, Good: Black, Warn: Black, Bad: Black, Accent: Black);

        // For a phone screen (Telegram): the colors show the state at a glance.
        public static readonly Palette Colored = new(
            Ink: Rgb(0x1F2328), Strong: Rgb(0x1F2328), Muted: Rgb(0x5F6B7A), Fill: Rgb(0xF1F4F8), Paper: White,
            HeaderMuted: Rgb(0xC5D3E3), CardMuted: Rgb(0x5F6B7A), CardBorder: false,
            Header: Rgb(0x1E3A5F),
            AlertNoneFill: Rgb(0xE3F4E8), AlertNoneText: Rgb(0x1B6B34),
            DroneFill: Rgb(0xF5B000), DroneText: Rgb(0x1F2328),
            MissileFill: Rgb(0xD32F2F), MissileText: White,
            Good: Rgb(0x2E7D32), Warn: Rgb(0xD97706), Bad: Rgb(0xD32F2F), Accent: Rgb(0x1E3A5F));

        private static Color Rgb(int rgb) =>
            Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
