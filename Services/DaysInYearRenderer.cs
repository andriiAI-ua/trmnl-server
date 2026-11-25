using Trmnl.Server.Services;

/// <summary>
/// Draws the old screen: the date and the days that passed and that are left in the year.
/// </summary>
public sealed class DaysInYearRenderer
{
    private readonly MarkdownResourceProvider _markdown;
    private readonly TemplateProcessor _templates;
    private readonly ImageRenderer _renderer;

    public DaysInYearRenderer(
        MarkdownResourceProvider markdown,
        TemplateProcessor templates,
        ImageRenderer renderer)
    {
        _markdown = markdown;
        _templates = templates;
        _renderer = renderer;
    }

    public async Task<byte[]> RenderAsync(DateTime date, int width, int height, CancellationToken cancellationToken)
    {
        var day = date.Date;
        var markdownText = await _markdown.GetMarkdownAsync("DaysInYear", cancellationToken);
        var md = _templates.EvaluateTemplate(markdownText, contextObject: new
        {
            now = day,
            passed = day.DayOfYear,
            left = (DateTime.IsLeapYear(day.Year) ? 366 : 365) - day.DayOfYear
        });

        return _renderer.RenderMarkdown(md, width, height);
    }
}
