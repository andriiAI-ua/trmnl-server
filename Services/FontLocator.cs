/// <summary>
/// Finds the font files of the application.
/// </summary>
public static class FontLocator
{
    /// <summary>
    /// Return the path of a font file. Look in TRMNL_FONT_DIR, then in the Fonts folder of the content root.
    /// </summary>
    public static string Find(IHostEnvironment env, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("TRMNL_FONT_DIR") ?? "", fileName),
            Path.Combine(env.ContentRootPath, "Fonts", fileName),
            Path.Combine(AppContext.BaseDirectory, "Fonts", fileName),
            $"/opt/trmnl-server/Fonts/{fileName}",
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Font not found: {fileName}", fileName);
    }
}
