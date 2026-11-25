using Markdig;
using Markdig.Syntax;

public sealed class MarkdownTemplate
{
    private readonly MarkdownPipeline _pipe;

    public MarkdownTemplate()
    {
        _pipe =
            new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
    }

    public MarkdownDocument Parse(string md) =>
        Markdown.Parse(md, _pipe);
}
