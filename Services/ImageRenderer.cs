using Markdig;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

public sealed class ImageRenderer
{
    // The font sizes of MarkdownRenderer are for a screen of this width.
    private const int BaseWidth = 800;

    private readonly MarkdownPipeline _pipeline;
    private readonly MarkdownRenderer _md;

    public ImageRenderer(IHostEnvironment env)
    {
        _pipeline =
            new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();

        _md = new MarkdownRenderer(env);
    }

    public byte[] RenderMarkdown(
        string markdown,
        int width = 800,
        int height = 480)
    {
        var doc =
            Markdown.Parse(markdown, _pipeline);

        // Draw a smaller screen at a larger size, then make it smaller. Then the text fits.
        var scale = width < BaseWidth ? (double)BaseWidth / width : 1;

        using var img =
            _md.Render(doc, (int)Math.Round(width * scale), (int)Math.Round(height * scale));

        if (scale != 1)
            img.Mutate(x => x.Resize(width, height));

        using var ms =
            new MemoryStream();

        img.SaveAsPng(ms);

        return ms.ToArray();
    }
}
