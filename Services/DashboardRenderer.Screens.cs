using System.Globalization;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>
/// The data of the screens other than the dashboard.
/// </summary>
/// <param name="Now">The local time of the data. The clock screen shows it.</param>
/// <param name="Days">The 10-day forecast from today, or null.</param>
/// <param name="Room">The room climate, or null.</param>
/// <param name="Note">The text of the note screen.</param>
public sealed record ScreenExtras(DateTime Now, IReadOnlyList<CityForecastDay>? Days, RoomClimate? Room, string Note)
{
    /// <summary>The values that change the image of the screen, in addition to the dashboard view.</summary>
    public string Fingerprint(ScreenKind kind) => kind switch
    {
        ScreenKind.Quad5 or ScreenKind.Days10 => string.Join(";",
            (Days ?? []).Select(d => $"{d.Date:yyyyMMdd}:{d.Night?.Temperature},{d.Night?.Symbol}/{d.Day?.Temperature},{d.Day?.Symbol}")),
        ScreenKind.Room => $"{Room?.Temperature:0.0}/{Room?.Humidity:0}",
        ScreenKind.Clock => $"{Now:yyyyMMddHHmm}/{Room?.Temperature:0.0}/{Room?.Humidity:0}",
        ScreenKind.Note => Note,
        _ => ""
    };
}

/// <summary>
/// The 800x480 screens that repeat the screens of the RLCD device (rlcd-dashboard, 400x300). They are drawn
/// on a virtual area of 500x300 at the scale 1.6, so that the sizes of the RLCD layouts stay the same.
/// The device shows the image some minutes, so these screens show the date, not the time. Only the clock
/// screen shows the time: with an update each 5 minutes, the hands are correct to 5 minutes.
/// </summary>
public sealed partial class DashboardRenderer
{
    private const float AreaWidth = 500;
    private const float AreaHeight = 300;
    private const float AreaScale = Width / AreaWidth;

    /// <summary>Draw a screen at 800x480 as a PNG: 1-bit, or 4 gray levels if the view is in gray.</summary>
    public byte[] RenderScreen(ScreenKind kind, DashboardView view, ScreenExtras extras, DateTime updatedAt)
    {
        var grayscale = view.Grayscale;
        var p = grayscale ? Palette.Gray : Palette.Mono;

        using var img = new Image<Rgba32>(Width, Height);
        img.Mutate(ctx =>
        {
            ctx.Fill(p.Paper);
            if (kind == ScreenKind.Clock)
            {
                DrawClock(ctx, view, extras, p);
            }
            else
            {
                ctx.SetDrawingTransform(Matrix3x2.CreateScale(AreaScale));
                switch (kind)
                {
                    case ScreenKind.Quad:
                        DrawQuadScreen(ctx, view, extras, p);
                        break;
                    case ScreenKind.Quad5:
                        DrawQuad5(ctx, view, extras, p);
                        break;
                    case ScreenKind.Days10:
                        DrawDays10(ctx, view, extras, p);
                        break;
                    case ScreenKind.Room:
                        DrawRoom(ctx, view, extras, p, updatedAt);
                        break;
                    default:
                        DrawNote(ctx, view, extras, p, updatedAt);
                        break;
                }
            }

            if (!grayscale)
                ctx.BinaryThreshold(0.5f);
        });

        using var ms = new MemoryStream();
        if (grayscale)
        {
            QuantizeToFourGrays(img);
            img.SaveAsPng(ms);
        }
        else
        {
            img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit1 });
        }

        return ms.ToArray();
    }

    /* ===============================
       Four quadrants
       =============================== */

    private const float QuadWidth = AreaWidth / 2;
    private const float QuadHeight = AreaHeight / 2;

    private void DrawQuadScreen(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        DrawQuadDate(ctx, v, e, p);
        DrawQuadAir(ctx, v, p, QuadWidth + QuadWidth / 2);
        DrawQuadAlert(ctx, v, p, new RectangleF(0, QuadHeight, QuadWidth, QuadHeight));
        DrawQuadPower(ctx, v, p, new RectangleF(QuadWidth, QuadHeight, QuadWidth, QuadHeight));
        DrawQuadBattery(ctx, v, p);
    }

    // Top left, black: the date ("23.11") and the weekday. The RLCD device shows the time here, but this image
    // stays on the screen some minutes.
    private void DrawQuadDate(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        const float cx = QuadWidth / 2;
        ctx.Fill(p.Ink, new RectangularPolygon(0, 0, QuadWidth, QuadHeight));
        Text(ctx, $"{e.Now:d.MM}", Bold(72), p.Paper, cx, 84, HorizontalAlignment.Center);
        Text(ctx, v.Weekday, FitBold(v.Weekday, 30, QuadWidth - 30), p.Paper, cx, 126, HorizontalAlignment.Center);
        if (v.BatteryLow)
            Text(ctx, "Потрібно зарядити", Bold(14), p.Paper, cx, QuadHeight - 8, HorizontalAlignment.Center);
    }

    // The battery at the top of the white quadrant: the icon at the left, the percent at the right,
    // as on the RLCD device. The outdoor temperature is 5 px lower for this row.
    private void DrawQuadBattery(IImageProcessingContext ctx, DashboardView v, Palette p)
    {
        if (v.BatteryPercent is not { } percent)
            return;

        Battery(ctx, QuadWidth + 8, 6, percent, p.Ink, p.Paper);
        Text(ctx, $"{percent} %", Bold(14), p.Ink, AreaWidth - 8, 19, HorizontalAlignment.Right);
    }

    /* ===============================
       Five panels: the forecast for 3 days
       =============================== */

    private void DrawQuad5(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        DrawQuadDate(ctx, v, e, p);
        DrawQuadAir(ctx, v, p, QuadWidth + QuadWidth / 2);
        DrawQuadBattery(ctx, v, p);

        // A line under the white quadrant, and lines between the panels.
        ctx.Fill(p.Ink, new RectangularPolygon(QuadWidth, QuadHeight, QuadWidth, 2));
        for (var i = 0; i < 3; i++)
        {
            var x = i * AreaWidth / 3;
            var w = (i + 1) * AreaWidth / 3 - x;
            if (i > 0)
                ctx.Fill(p.Ink, new RectangularPolygon(x - 1, QuadHeight + 10, 2, QuadHeight - 20));
            DrawForecastPanel(ctx, e, i, x, w, p);
        }
    }

    // A panel: the name of the day and its date, the night and the day (icon and temperature, as on
    // meteo.gov.ua) and the weather of the day in words.
    private void DrawForecastPanel(IImageProcessingContext ctx, ScreenExtras e, int i, float x, float w, Palette p)
    {
        const float y = QuadHeight;
        var cx = x + w / 2;
        var date = e.Now.Date.AddDays(i);
        var title = i switch { 0 => "Сьогодні", 1 => "Завтра", _ => Capitalize(date.ToString("dddd", Culture)) };
        Text(ctx, title, FitBold(title, 20, w - 8), p.Ink, cx, y + 24, HorizontalAlignment.Center);
        Text(ctx, date.ToString("d MMMM", Culture), Regular(13), p.Ink, cx, y + 42, HorizontalAlignment.Center);

        var day = e.Days?.ElementAtOrDefault(i);
        if (day is not { Known: true })
        {
            Text(ctx, "немає прогнозу", Regular(14), p.Ink, cx, y + 96, HorizontalAlignment.Center);
            return;
        }

        DrawDayPoint(ctx, cx - w / 4, y + 68, y + 112, Bold(24), day.Night, p);
        DrawDayPoint(ctx, cx + w / 4, y + 68, y + 112, Bold(24), day.Day, p);

        var text = WeatherText((day.Day ?? day.Night)!.Symbol);
        var lines = WrapText(text, Regular(13), w - 10, maxLines: 2);
        for (var k = 0; k < lines.Count; k++)
            Text(ctx, lines[k], Regular(13), p.Ink, cx, y + 130 + k * 15, HorizontalAlignment.Center);
    }

    // The icon and the temperature of the night or the day. Without a value: a dash.
    private void DrawDayPoint(
        IImageProcessingContext ctx, float cx, float iconCy, float baseline, Font font, ForecastPoint? point, Palette p)
    {
        if (point is null)
        {
            Text(ctx, "—", font, p.Ink, cx, baseline, HorizontalAlignment.Center);
            return;
        }

        WeatherIcon(ctx, cx, iconCy, point.Symbol, p);
        Text(ctx, Degrees(point.Temperature), font, p.Ink, cx, baseline, HorizontalAlignment.Center);
    }

    // "+1°", "0°", "−4°".
    private static string Degrees(int t) => t switch { > 0 => $"+{t}°", < 0 => $"−{-t}°", _ => "0°" };

    /* ===============================
       10 days
       =============================== */

    private const float CellWidth = AreaWidth / 4;
    private const float CellHeight = AreaHeight / 3;

    // 12 squares: the date, the weather now and 10 days of the forecast (the night and the day).
    private void DrawDays10(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        DrawDaysDateCell(ctx, v, e, p);
        DrawDaysNowCell(ctx, v, p, CellWidth, 0);

        // Without a forecast the two squares at the top right tell why; the other days show dashes.
        var none = e.Days is null || e.Days.All(d => !d.Known);
        if (none)
            Text(ctx, "немає прогнозу", Regular(14), p.Ink, 3 * CellWidth, CellHeight / 2 + 6, HorizontalAlignment.Center);

        for (var i = 0; i < MeteoCityForecastClient.Days; i++)
        {
            var n = i + 2;
            if (none && n < 4)
                continue;
            DrawDaysCell(ctx, e, i, n % 4 * CellWidth, n / 4 * CellHeight, p);
        }

        // The lines between the squares.
        for (var k = 1; k < 4; k++)
        {
            var top = k == 1 || (k == 3 && none) ? CellHeight : 0;
            ctx.Fill(p.Ink, new RectangularPolygon(k * CellWidth - 0.5f, top, 1.25f, AreaHeight - top));
        }
        ctx.Fill(p.Ink, new RectangularPolygon(CellWidth, CellHeight - 0.5f, AreaWidth - CellWidth, 1.25f));
        ctx.Fill(p.Ink, new RectangularPolygon(0, 2 * CellHeight - 0.5f, AreaWidth, 1.25f));
    }

    // Top left, black: the weekday, the date and the battery.
    private void DrawDaysDateCell(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        const float cx = CellWidth / 2;
        ctx.Fill(p.Ink, new RectangularPolygon(0, 0, CellWidth, CellHeight));
        var weekday = Capitalize(v.Weekday);
        Text(ctx, weekday, FitBold(weekday, 18, CellWidth - 10), p.Paper, cx, 22, HorizontalAlignment.Center);
        Text(ctx, $"{e.Now:d.MM}", Bold(40), p.Paper, cx, 64, HorizontalAlignment.Center);

        if (v.BatteryPercent is { } percent)
        {
            Battery(ctx, 10, 78, percent, p.Paper, p.Ink);
            Text(ctx, v.BatteryLow ? $"{percent} %!" : $"{percent} %", Bold(14), p.Paper, CellWidth - 10, 91,
                HorizontalAlignment.Right);
        }
    }

    // The weather now: the outdoor temperature, the humidity and the AQI.
    private void DrawDaysNowCell(IImageProcessingContext ctx, DashboardView v, Palette p, float x, float y)
    {
        var cx = x + CellWidth / 2;
        Text(ctx, "Зараз", Bold(15), p.Ink, cx, y + 18, HorizontalAlignment.Center);
        Text(ctx, v.Temperature, FitBold(v.Temperature, 30, CellWidth - 8), p.Ink, cx, y + 54, HorizontalAlignment.Center);
        if (v.Humidity != "н/д")
            Text(ctx, $"вологість {v.Humidity}", Regular(13), p.Ink, cx, y + 74, HorizontalAlignment.Center);
        if (v.AqiLevel is not null)
            Text(ctx, $"AQI {v.AirQuality}", Regular(13), p.Ink, cx, y + 91, HorizontalAlignment.Center);
    }

    // A day: "Сьогодні", "Завтра" or "Ср, 26.11"; the night and the day under it.
    private void DrawDaysCell(IImageProcessingContext ctx, ScreenExtras e, int i, float x, float y, Palette p)
    {
        var cx = x + CellWidth / 2;
        var date = e.Now.Date.AddDays(i);
        var title = i switch
        {
            0 => "Сьогодні",
            1 => "Завтра",
            _ => $"{Capitalize(date.ToString("ddd", Culture))}, {date:d.MM}"
        };
        Text(ctx, title, FitBold(title, 15, CellWidth - 8), p.Ink, cx, y + 17, HorizontalAlignment.Center);

        var day = e.Days?.ElementAtOrDefault(i);
        if (day is not { Known: true })
        {
            Text(ctx, "—", Bold(24), p.Ink, cx, y + 66, HorizontalAlignment.Center);
            return;
        }

        DrawDayPoint(ctx, cx - 30, y + 45, y + 89, Bold(20), day.Night, p);
        DrawDayPoint(ctx, cx + 30, y + 45, y + 89, Bold(20), day.Day, p);
    }

    /* ===============================
       Text and room (the compact header, a card, the compact footer)
       =============================== */

    private const float CardTop = 106;
    private const float CardHeight = 156;

    private void DrawNote(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p, DateTime updatedAt)
    {
        const float m = CompactMargin;
        DrawCompactHeader(ctx, v, p, new RectangleF(m, m, AreaWidth - 2 * m, 94));
        var box = new RectangleF(m, CardTop, AreaWidth - 2 * m, CardHeight);
        Card(ctx, box, p, radius: 10);

        var text = e.Note.Trim();
        if (text.Length == 0)
        {
            Text(ctx, "Текст не задано", Regular(17), p.Ink, AreaWidth / 2, box.Top + box.Height / 2, HorizontalAlignment.Center);
            Text(ctx, "введіть його в Home Assistant", Regular(14), p.CardMuted, AreaWidth / 2,
                box.Top + box.Height / 2 + 22, HorizontalAlignment.Center);
        }
        else
        {
            // The largest font that shows all the text: the shorter the text, the larger the letters.
            const float pad = 12;
            var areaWidth = box.Width - 2 * pad;
            var areaHeight = box.Height - 12;
            var fonts = new[] { Bold(48), Bold(40), Bold(34), Bold(30), Bold(26), Bold(22), Regular(18), Regular(15), Regular(13) };
            Font font = fonts[^1];
            List<string> lines = [];
            var lineHeight = 16f;
            foreach (var f in fonts)
            {
                font = f;
                lineHeight = f.Size * 1.25f;
                lines = WrapText(text, f, areaWidth, maxLines: int.MaxValue);
                if (lines.Count * lineHeight <= areaHeight)
                    break;
            }

            // The smallest font: show the lines that fit.
            var count = Math.Min(lines.Count, (int)(areaHeight / lineHeight));
            var top = box.Top + 6 + (areaHeight - count * lineHeight) / 2;
            for (var i = 0; i < count; i++)
                Text(ctx, lines[i], font, p.Ink, box.Left + pad, top + i * lineHeight + lineHeight * 0.85f);
        }

        DrawCompactFooter(ctx, v, p, updatedAt, AreaWidth, AreaHeight);
    }

    private void DrawRoom(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p, DateTime updatedAt)
    {
        const float m = CompactMargin;
        DrawCompactHeader(ctx, v, p, new RectangleF(m, m, AreaWidth - 2 * m, 94));
        var box = new RectangleF(m, CardTop, AreaWidth - 2 * m, CardHeight);
        Card(ctx, box, p, radius: 10);

        var left = box.Left + 14;
        var middle = box.Left + box.Width / 2 + 14;
        Text(ctx, "У кімнаті", Bold(18), p.Ink, left, box.Top + 26);
        Text(ctx, "Температура", Regular(14), p.CardMuted, left, box.Top + 54);
        Text(ctx, "Вологість", Regular(14), p.CardMuted, middle, box.Top + 54);
        Text(ctx, e.Room?.Temperature is { } t ? FormatTemperature(t) : "—", Bold(44), p.Ink, left, box.Top + 104);
        Text(ctx, e.Room?.Humidity is { } h ? $"{Math.Round(h):0} %" : "—", Bold(44), p.Ink, middle, box.Top + 104);

        // The outdoor air under the room values.
        var parts = new List<string>();
        if (v.Humidity != "н/д")
            parts.Add($"вологість {v.Humidity}");
        if (v.AqiLevel is not null)
            parts.Add($"AQI {v.AirQuality}, {v.AirQualityNote}");
        if (parts.Count > 0)
            Text(ctx, $"Надворі: {string.Join(" · ", parts)}", Regular(14), p.CardMuted, left, box.Top + 140);

        DrawCompactFooter(ctx, v, p, updatedAt, AreaWidth, AreaHeight);
    }

    // Break a text into lines that fit maxWidth: at the spaces, and at each line break.
    // A word wider than the line stays on its own line.
    private static List<string> WrapText(string text, Font font, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var options = new TextOptions(font);
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length == 0 || TextMeasurer.MeasureAdvance(candidate, options).Width <= maxWidth)
                {
                    line = candidate;
                }
                else
                {
                    lines.Add(line);
                    line = word;
                }
            }
            lines.Add(line);
        }

        // Remove the empty lines at the end (a text that ends with a line break).
        while (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return lines.Count > maxLines ? lines.Take(maxLines).ToList() : lines;
    }

    /* ===============================
       Analog clock
       =============================== */

    // The clock face at the left (480x480), a black panel with the date and the weather at the right.
    // The hands show the time of the image: with an update each 5 minutes they are correct to 5 minutes.
    private void DrawClock(IImageProcessingContext ctx, DashboardView v, ScreenExtras e, Palette p)
    {
        const float cx = 240, cy = 240, r = 222;

        // The face: a ring, 60 minute marks, 12 hour marks, and the numbers 12, 3, 6 and 9.
        ctx.Draw(p.Ink, 6, new EllipsePolygon(cx, cy, r));
        for (var i = 0; i < 60; i++)
        {
            var hour = i % 5 == 0;
            var a = i * MathF.PI / 30;
            var (sin, cos) = MathF.SinCos(a);
            var outer = r - 12;
            var inner = hour ? r - (i % 15 == 0 ? 52 : 42) : r - 22;
            ctx.DrawLine(p.Ink, hour ? (i % 15 == 0 ? 11 : 7) : 3,
                new PointF(cx + sin * inner, cy - cos * inner), new PointF(cx + sin * outer, cy - cos * outer));
        }
        var numbers = Bold(44);
        var nr = r - 86;
        foreach (var (n, angle) in new[] { (12, 0f), (3, 90f), (6, 180f), (9, 270f) })
        {
            var (sin, cos) = MathF.SinCos(angle * MathF.PI / 180);
            Text(ctx, n.ToString(CultureInfo.InvariantCulture), numbers, p.Ink, cx + sin * nr, cy - cos * nr + 16,
                HorizontalAlignment.Center);
        }

        // The hands: the hour hand moves with the minutes.
        var time = e.Now;
        var minutes = time.Minute + time.Second / 60f;
        var hours = time.Hour % 12 + minutes / 60f;
        Hand(ctx, cx, cy, hours * MathF.PI / 6, r * 0.52f, 22, 20, 9, p.Ink);
        Hand(ctx, cx, cy, minutes * MathF.PI / 30, r * 0.82f, 26, 14, 6, p.Ink);
        ctx.Fill(p.Ink, new EllipsePolygon(cx, cy, 16));
        ctx.Fill(p.Paper, new EllipsePolygon(cx, cy, 5));

        // The black panel: the weekday, the date, the outdoor temperature and the room climate.
        const float left = 480, pcx = 640;
        ctx.Fill(p.Ink, new RectangularPolygon(left, 0, Width - left, Height));
        var weekday = Capitalize(v.Weekday);
        Text(ctx, weekday, FitBold(weekday, 38, 290), p.Paper, pcx, 64, HorizontalAlignment.Center);
        Text(ctx, time.Day.ToString(CultureInfo.InvariantCulture), Bold(120), p.Paper, pcx, 186, HorizontalAlignment.Center);
        Text(ctx, Culture.DateTimeFormat.MonthGenitiveNames[time.Month - 1], Regular(34), p.HeaderMuted, pcx, 234,
            HorizontalAlignment.Center);

        ctx.Fill(p.HeaderMuted, new RectangularPolygon(left + 40, 262, Width - left - 80, 2));
        Text(ctx, v.Temperature, FitBold(v.Temperature, 64, 290), p.Paper, pcx, 336, HorizontalAlignment.Center);
        var outdoor = v.Humidity == "н/д" ? "надворі" : $"надворі · вологість {v.Humidity}";
        Text(ctx, outdoor, Regular(22), p.HeaderMuted, pcx, 368, HorizontalAlignment.Center);

        if (e.Room is { } room && (room.Temperature is not null || room.Humidity is not null))
        {
            var values = new List<string>();
            if (room.Temperature is { } t)
                values.Add(FormatTemperature(t));
            if (room.Humidity is { } h)
                values.Add($"{Math.Round(h):0} %");
            Text(ctx, $"у кімнаті {string.Join(" · ", values)}", Regular(22), p.HeaderMuted, pcx, 404,
                HorizontalAlignment.Center);
        }

        if (v.BatteryPercent is { } percent)
        {
            var label = v.BatteryLow ? $"Зарядіть · {percent} %" : $"{percent} %";
            var font = v.BatteryLow ? Bold(20) : Regular(20);
            var w = TextMeasurer.MeasureAdvance(label, new TextOptions(font)).Width;
            var x = pcx - (w + 40) / 2;
            Battery(ctx, x, Height - 36, percent, p.Paper, p.Ink);
            Text(ctx, label, font, p.Paper, x + 40, Height - 22);
        }
    }

    // A hand from the center: it starts "tail" before the center and gets narrow to the tip.
    // The angle is from 12 o'clock, clockwise, in radians.
    private static void Hand(
        IImageProcessingContext ctx, float cx, float cy, float angle, float length, float tail, float baseWidth,
        float tipWidth, Color color)
    {
        var (sin, cos) = MathF.SinCos(angle);
        var dir = new Vector2(sin, -cos);
        var normal = new Vector2(cos, sin);
        var center = new Vector2(cx, cy);
        var back = center - dir * tail;
        var tip = center + dir * length;
        PointF P(Vector2 v) => new(v.X, v.Y);
        ctx.Fill(color, new Polygon(new LinearLineSegment(
            P(back - normal * baseWidth / 2),
            P(tip - normal * tipWidth / 2),
            P(tip + normal * tipWidth / 2),
            P(back + normal * baseWidth / 2))));
    }

    /* ===============================
       Weather icons (as on the RLCD device: 34 x 34 px around the center)
       =============================== */

    private static void Disc(IImageProcessingContext ctx, float cx, float cy, float r, Color color) =>
        ctx.Fill(color, new EllipsePolygon(cx, cy, r + 0.5f));

    // A cloud of three circles on a flat bottom, 27 x 18 px around (cx, cy). grow makes it larger on all sides.
    private static void CloudShape(IImageProcessingContext ctx, float cx, float cy, float grow, Color color)
    {
        Disc(ctx, cx - 7, cy + 2, 5 + grow, color);
        Disc(ctx, cx + 1, cy - 2, 8 + grow, color);
        Disc(ctx, cx + 8, cy + 2, 5 + grow, color);
        ctx.Fill(color, new RectangularPolygon(cx - 7, cy + 2, 16, 6 + grow));
    }

    // A white cloud with a black line of 2 px. It covers what is under it (the sun or the moon).
    private static void Cloud(IImageProcessingContext ctx, float cx, float cy, Palette p)
    {
        CloudShape(ctx, cx, cy, 2, p.Ink);
        CloudShape(ctx, cx, cy, 0, p.Paper);
    }

    private static void Sun(IImageProcessingContext ctx, float cx, float cy, float r, Palette p)
    {
        Disc(ctx, cx, cy, r, p.Ink);
        for (var k = 0; k < 8; k++)
        {
            var (s, c) = MathF.SinCos(k * MathF.PI / 4);
            ctx.DrawLine(p.Ink, 2,
                new PointF(cx + c * (r + 3), cy + s * (r + 3)), new PointF(cx + c * (r + 6.5f), cy + s * (r + 6.5f)));
        }
    }

    // A crescent: a black disc, and a white disc over its top right part.
    private static void Moon(IImageProcessingContext ctx, float cx, float cy, float r, Palette p)
    {
        Disc(ctx, cx, cy, r, p.Ink);
        Disc(ctx, cx + r * 2 / 3, cy - r / 3, r * 4 / 5, p.Paper);
    }

    private static void SkyBody(IImageProcessingContext ctx, float cx, float cy, float r, bool night, Palette p)
    {
        if (night)
            Moon(ctx, cx, cy, r + 2, p);
        else
            Sun(ctx, cx, cy, r, p);
    }

    // The marks of the precipitation, 9 px high from y.
    private static void Drop(IImageProcessingContext ctx, float x, float y, Palette p) =>
        ctx.Fill(p.Ink, new Polygon(new LinearLineSegment(
            new PointF(x + 3, y), new PointF(x + 6, y), new PointF(x + 3, y + 9), new PointF(x, y + 9))));

    private static void Flake(IImageProcessingContext ctx, float x, float y, Palette p)
    {
        ctx.Fill(p.Ink, new RectangularPolygon(x, y, 2, 9));
        ctx.DrawLine(p.Ink, 2, new PointF(x - 3, y + 2.5f), new PointF(x + 5, y + 6.5f));
        ctx.DrawLine(p.Ink, 2, new PointF(x - 3, y + 6.5f), new PointF(x + 5, y + 2.5f));
    }

    private static void Dot(IImageProcessingContext ctx, float x, float y, Palette p) => Disc(ctx, x + 1, y + 4, 3, p.Ink);

    private enum Mark { None, Drop, Sleet, Flake, Dot, Bolt }

    /// <summary>
    /// Draw the icon of an FMI SmartSymbol: 1..99 by day, the same + 100 at night.
    /// 1 clear, 2 mostly clear, 4 partly cloudy, 6 mostly cloudy, 7 overcast, 9 fog; 11 drizzle,
    /// 14 freezing drizzle, 17 freezing rain; 21..27 showers, 31..39 rain, 41..49 sleet, 51..59 snow,
    /// 61..67 hail, 71..77 thunder. In these groups the last digit gives the cover (1..3 isolated, 4..6 scattered,
    /// 7..9 everywhere) and the intensity (1, 4, 7 light; 2, 5, 8 moderate; 3, 6, 9 heavy).
    /// </summary>
    private static void WeatherIcon(IImageProcessingContext ctx, float cx, float cy, int symbol, Palette p)
    {
        var night = symbol > 100;
        var b = symbol % 100;
        switch (b)
        {
            case <= 0:
                return;
            case 1:
                SkyBody(ctx, cx, cy, 8, night, p);
                return;
            case 2 or 4:
                SkyBody(ctx, cx - 6, cy - 6, 6, night, p);
                Cloud(ctx, cx + 4, cy + 5, p);
                return;
            case 6:
                SkyBody(ctx, cx - 8, cy - 8, 5, night, p);
                Cloud(ctx, cx + 2, cy + 3, p);
                return;
            case 7:
                Cloud(ctx, cx, cy + 2, p);
                return;
            case 9:
                Cloud(ctx, cx, cy - 5, p);
                ctx.Fill(p.Ink, new RectangularPolygon(cx - 13, cy + 9, 22, 2));
                ctx.Fill(p.Ink, new RectangularPolygon(cx - 9, cy + 14, 22, 2));
                return;
        }

        var mark = Mark.Drop;
        var count = 2;
        var body = false; // the sun or the moon behind the cloud: isolated and scattered showers
        if (b < 20)
        {
            mark = b == 11 ? Mark.Dot : Mark.Drop;
            count = b == 11 ? 3 : 2;
        }
        else
        {
            var unit = b % 10;
            var cover = unit >= 1 ? (unit - 1) / 3 : 2;
            var intensity = unit >= 1 ? (unit - 1) % 3 : 0;
            body = cover < 2;
            (mark, count) = (b / 10) switch
            {
                2 => (Mark.Drop, 2),
                3 => (Mark.Drop, intensity + 1),
                4 => (Mark.Sleet, intensity + 2),
                5 => (Mark.Flake, intensity + 1),
                6 => (Mark.Dot, 2),
                7 => (Mark.Bolt, 1),
                _ => (Mark.None, 0)
            };
        }

        var ccx = cx;
        if (body)
        {
            SkyBody(ctx, cx - 7, cy - 11, 5, night, p);
            ccx = cx + 3;
        }
        Cloud(ctx, ccx, cy - 5, p);
        if (mark == Mark.Bolt)
        {
            Bolt(ctx, ccx - 4, cy + 5, 9, 12, p.Ink);
            return;
        }

        int[][] offsets = [[0], [-6, 6], [-11, 0, 11]];
        count = Math.Min(count, 3);
        for (var i = 0; i < count; i++)
        {
            var x = ccx + offsets[count - 1][i];
            var y = cy + 8;
            switch (mark)
            {
                case Mark.Drop:
                    Drop(ctx, x - 2, y, p);
                    break;
                case Mark.Flake:
                    Flake(ctx, x, y, p);
                    break;
                case Mark.Dot:
                    Dot(ctx, x, y, p);
                    break;
                case Mark.Sleet:
                    // Drops and flakes in turn.
                    if (i % 2 == 0)
                        Drop(ctx, x - 2, y, p);
                    else
                        Flake(ctx, x, y, p);
                    break;
            }
        }
    }

    /// <summary>The weather of an FMI SmartSymbol in words: "хмарно", "місцями невеликий сніг".</summary>
    public static string WeatherText(int symbol)
    {
        var b = symbol % 100;
        switch (b)
        {
            case 1: return "ясно";
            case 2: return "переважно ясно";
            case 4: return "мінлива хмарність";
            case 6: return "хмарно з проясненнями";
            case 7: return "хмарно";
            case 9: return "туман";
            case 11: return "мряка";
            case 14: return "крижана мряка";
            case 17: return "крижаний дощ";
        }

        if (b is < 21 or > 79 || b % 10 == 0)
            return "";

        var unit = b % 10;
        var cover = (unit - 1) / 3;
        var intensity = (unit - 1) % 3;
        var text = (b / 10) switch
        {
            2 => "короткочасний дощ",
            3 => new[] { "невеликий дощ", "дощ", "сильний дощ" }[intensity],
            4 => new[] { "невеликий мокрий сніг", "мокрий сніг", "сильний мокрий сніг" }[intensity],
            5 => new[] { "невеликий сніг", "сніг", "сильний сніг" }[intensity],
            6 => "град",
            _ => "гроза"
        };
        return cover < 2 ? $"місцями {text}" : text;
    }
}
