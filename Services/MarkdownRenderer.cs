using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Text;

public sealed class MarkdownRenderer
{
    private readonly FontFamily _font;

    public MarkdownRenderer(IHostEnvironment env)
    {
        var fonts = new FontCollection();
        _font = fonts.Add(FontLocator.Find(env, "Roboto-Regular.ttf"));
    }

    /* ===============================
       Public API
       =============================== */

    public Image<Rgba32> Render(
        MarkdownDocument doc,
        int width,
        int height)
    {
        var img =
            new Image<Rgba32>(width, height);

        img.Mutate(ctx =>
        {
            ctx.Fill(Color.White);

            RenderDocument(ctx, doc, width);
        });

        return img;
    }

    /* ===============================
       Layout
       =============================== */

    private void RenderDocument(
        IImageProcessingContext ctx,
        MarkdownDocument doc,
        int width)
    {
        var y = 40f;

        foreach (var block in doc)
        {
            y =
                block switch
                {
                    HeadingBlock h =>
                        RenderHeading(ctx, h, y, width),

                    ParagraphBlock p =>
                        RenderParagraph(ctx, p, y, width),

                    Table t =>
                        RenderTable(ctx, t, y, width),

                    _ => y
                };

            y += 24;
        }
        RenderGeneratedAt(ctx, width);
    }

    /* ===============================
       Blocks
       =============================== */

    private float RenderHeading(
        IImageProcessingContext ctx,
        HeadingBlock h,
        float y,
        int width)
    {
        var size =
            h.Level switch
            {
                1 => 72,
                2 => 56,
                3 => 44,
                _ => 36
            };

        var font =
            _font.CreateFont(size, FontStyle.Bold);

        var text =
            ExtractInlineText(h.Inline);

        return DrawCenteredWrapped(
            ctx,
            text,
            font,
            y,
            width);
    }

    private float RenderParagraph(
        IImageProcessingContext ctx,
        ParagraphBlock p,
        float y,
        int width)
    {
        var font =
            _font.CreateFont(34);

        var text =
            ExtractInlineText(p.Inline);

        return DrawLeftWrapped(
            ctx,
            text,
            font,
            y,
            width);
    }

    private float RenderTable(
        IImageProcessingContext ctx,
        Table table,
        float y,
        int width)
    {
        var font =
            _font.CreateFont(28);

        var colCount =
            table.ColumnDefinitions.Count;

        var colWidth =
            (width - 80) / colCount;

        var x0 = 40f;

        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row)
                continue;

            var x = x0;
            var rowHeight = 0f;

            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell)
                    continue;

                var text =
                    cell[0] is ParagraphBlock p ? ExtractInlineText(p.Inline) : string.Empty;

                var size =
                    TextMeasurer.MeasureBounds(
                        text,
                        new TextOptions(font));

                ctx.DrawText(
                    text,
                    font,
                    Color.Black,
                    new PointF(x, y));

                rowHeight =
                    Math.Max(rowHeight, size.Height);

                x += colWidth;
            }

            y += rowHeight + 16;
        }

        return y;
    }


    /* ===============================
       Text rendering (manual wrap)
       =============================== */

    private static float DrawCenteredWrapped(
        IImageProcessingContext ctx,
        string text,
        Font font,
        float y,
        int width)
    {
        var lines =
            WrapText(text, font, width - 80);

        foreach (var line in lines)
        {
            var size =
                TextMeasurer.MeasureBounds(
                    line,
                    new TextOptions(font));

            var x =
                (width - size.Width) / 2f;

            ctx.DrawText(
                line,
                font,
                Color.Black,
                new PointF(x, y));

            y += size.Height + 6;
        }

        return y;
    }

    private static float DrawLeftWrapped(
        IImageProcessingContext ctx,
        string text,
        Font font,
        float y,
        int width)
    {
        var lines =
            WrapText(text, font, width - 80);

        foreach (var line in lines)
        {
            var size =
                TextMeasurer.MeasureBounds(
                    line,
                    new TextOptions(font));

            ctx.DrawText(
                line,
                font,
                Color.Black,
                new PointF(40, y));

            y += size.Height + 6;
        }

        return y;
    }

    /* ===============================
       Word wrap
       =============================== */

    private static List<string> WrapText(
        string text,
        Font font,
        float maxWidth)
    {
        var words =
            text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        var lines =
            new List<string>();

        var current =
            new StringBuilder();

        foreach (var word in words)
        {
            var test =
                current.Length == 0
                    ? word
                    : current + " " + word;

            var size =
                TextMeasurer.MeasureBounds(
                    test,
                    new TextOptions(font));

            if (size.Width > maxWidth)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                current.Append(word);
            }
            else
            {
                if (current.Length > 0)
                    current.Append(' ');

                current.Append(word);
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }

    /* ===============================
       Markdown helpers
       =============================== */

    private static string ExtractInlineText(
        ContainerInline? inline)
    {
        if (inline is null)
            return string.Empty;

        var sb =
            new StringBuilder();

        foreach (var child in inline)
        {
            AppendInline(child, sb);
        }

        return sb.ToString();
    }

    private static void AppendInline(
        Inline inline,
        StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(
                    lit.Content.Text.AsSpan(
                        lit.Content.Start,
                        lit.Content.Length));
                break;

            case EmphasisInline emp:
                foreach (var c in emp)
                    AppendInline(c, sb);
                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case LinkInline link:
                foreach (var c in link)
                    AppendInline(c, sb);
                break;
        }
    }

    private void RenderGeneratedAt(
        IImageProcessingContext ctx,
        int width)
    {
        var font = _font.CreateFont(20);
        var text = $"згенеровано о {DateTime.Now:dd.MM.yyyy HH:mm}";

        var size = TextMeasurer.MeasureBounds(
            text,
            new TextOptions(font));

        // Отримуємо висоту зображення через властивість контексту
        var imgHeight = ctx.GetCurrentSize().Height;

        var x = width - size.Width - 40;
        var y = imgHeight - size.Height - 20;

        ctx.DrawText(
            text,
            font,
            Color.Gray,
            new PointF(x, y));
    }
}
