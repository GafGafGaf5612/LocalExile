using System.Text.Json;

/// <summary>
/// Reads and replaces the server's save files (characters, stashes, walk-map captures). On Windows another process (an
/// antivirus scan, the search indexer) can hold a file for a moment, so a read waits for the file to be free, and a write
/// goes to "&lt;file&gt;.tmp" and then replaces the file, waiting the same way. A file that stays unreadable throws instead of
/// counting as missing: the callers would otherwise start over and write over it.
/// </summary>
static class SaveFiles
{
    /// <summary>How long a read or a replace waits for a held file before it gives up.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>The file's text, or null when there is no such file. A file that stays held throws
    /// <see cref="UnreadableSaveException"/>.</summary>
    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Retry(path, () => File.ReadAllText(path)); }
        catch (FileNotFoundException) { return null; }
        catch (Exception e) when (Held(e)) { throw Unreadable(path, e); }
    }

    /// <summary>The saved object, or null when there is no such file. A file that can't be read or isn't one (broken
    /// JSON, "null") throws <see cref="UnreadableSaveException"/> and is left as it is: treating it as missing would
    /// start the character or stash over and write over it.</summary>
    public static T? Load<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (Read(path) is not { } text) return null;
        T? value;
        try { value = JsonSerializer.Deserialize<T>(text, options); }
        catch (JsonException e) { throw Unreadable(path, e); }
        return value ?? throw Unreadable(path, null);
    }

    static UnreadableSaveException Unreadable(string path, Exception? e)
    {
        var error = new UnreadableSaveException(path, e);
        Console.WriteLine($"[gateway] {error.Message}: fix or remove it to use it again");
        return error;
    }

    /// <summary>Writes the text to "&lt;path&gt;.tmp", then replaces the file with it, so a crash leaves one whole file.</summary>
    public static void Write(string path, string text)
    {
        string tmp = path + ".tmp";
        Retry(tmp, () => { File.WriteAllText(tmp, text); return 0; });
        Retry(path, () => { File.Move(tmp, path, overwrite: true); return 0; });
    }

    static T Retry<T>(string path, Func<T> io)
    {
        var until = DateTime.UtcNow + Patience;
        for (int tries = 1; ; tries++)
        {
            try { return io(); }
            catch (Exception e) when (Held(e) && DateTime.UtcNow < until)
            {
                if (tries == 1) Console.WriteLine($"[gateway] {path} is held by another program ({e.Message}); waiting");
                Thread.Sleep(50);
            }
        }
    }

    // Sharing violations and denied access while another process has the file open; a missing file or folder is not
    // something waiting fixes.
    static bool Held(Exception e) =>
        e is UnauthorizedAccessException || e is IOException and not FileNotFoundException and not DirectoryNotFoundException;
}

/// <summary>A save file exists but can't be read. It is left as it is, and what needed it stops (a login, a change).</summary>
sealed class UnreadableSaveException(string path, Exception? inner)
    : IOException($"{path} can't be read{(inner is null ? "" : $" ({inner.Message})")}; it is left as it is", inner)
{
    public string Path { get; } = path;
}
