public class ImageCache
{
    private readonly string _dir;

    public ImageCache(IHostEnvironment env)
    {
        _dir = Path.Combine(
            env.ContentRootPath,
            "wwwroot",
            "screens"
        );

        Directory.CreateDirectory(_dir);
    }

    public string PathOf(string key)
        => Path.Combine(_dir, $"{key}.png");

    public bool Exists(string key)
        => File.Exists(PathOf(key));

public async Task EnsureSavedAsync(
    string key,
    byte[] data)
{
    var finalPath =
        PathOf(key);

    if (File.Exists(finalPath))
        return;

    var tmpPath =
        finalPath + "." +
        Guid.NewGuid().ToString("N") +
        ".tmp";

    try
    {
        await File.WriteAllBytesAsync(
            tmpPath,
            data);

        // publish atomically
        if (!File.Exists(finalPath))
        {
            File.Move(
                tmpPath,
                finalPath);
        }
        else
        {
            File.Delete(tmpPath);
        }
    }
    catch
    {
        if (File.Exists(tmpPath))
            File.Delete(tmpPath);

        throw;
    }
}

    // Keep the screens of this period. The device needs only the last screen.
    private static readonly TimeSpan MaxScreenAge = TimeSpan.FromDays(2);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private long _lastPruneTicks;

    /// <summary>
    /// Delete the screens that are older than 2 days. Do this not more than one time in an hour.
    /// </summary>
    public void PruneOldScreens()
    {
        var now = DateTime.UtcNow;
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.Ticks - last < PruneInterval.Ticks
            || Interlocked.CompareExchange(ref _lastPruneTicks, now.Ticks, last) != last)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_dir, "*.png"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > MaxScreenAge)
                    File.Delete(file);
            }
            catch (IOException)
            {
                // Try again at the next prune.
            }
            catch (UnauthorizedAccessException)
            {
                // Try again at the next prune.
            }
        }
    }
}
