using System.Text.Json;

namespace GameServer.Core.Saves;

/// <summary>
/// Reads and replaces the server's save files (characters, stashes, walk-map captures). On Windows another process (an
/// antivirus scan, the search indexer) can hold a file for a moment, so a read waits for the file to be free, and a write
/// goes to "&lt;file&gt;.tmp" and then replaces the file, waiting the same way. A file that stays unreadable throws instead of
/// counting as missing: the callers would otherwise start over and write over it.
/// </summary>
public static class SaveFiles
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
        Console.WriteLine($"[saves] {error.Message}: fix or remove it to use it again" +
            (File.Exists(path + ".bak") ? $" (the version before its last save is {path}.bak)" : ""));
        return error;
    }

    /// <summary>
    /// Writes the text to a temporary file next to <paramref name="path"/> (its bytes forced to the disk), keeps the version
    /// it replaces as "&lt;path&gt;.bak", then renames the temporary file over <paramref name="path"/>. A crash or power cut at
    /// any point leaves <paramref name="path"/> whole, old or new; a write that fails leaves the old file as it was. Callers
    /// that can write the same file from several threads must do so under one lock, so their copies land in order.
    /// </summary>
    public static void Write(string path, string text)
    {
        // Its own name per write: two writers never take each other's temporary file.
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Retry(tmp, () => { WriteThrough(tmp, text); return 0; });
            if (File.Exists(path)) Retry(path, () => { File.Copy(path, path + ".bak", overwrite: true); return 0; });
            Retry(path, () => { File.Move(tmp, path, overwrite: true); return 0; });
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // The bytes are on the disk before the rename that makes them the save, not only in the operating system's cache.
    static void WriteThrough(string path, string text)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(System.Text.Encoding.UTF8.GetBytes(text));
        file.Flush(flushToDisk: true);
    }

    static T Retry<T>(string path, Func<T> io)
    {
        var until = DateTime.UtcNow + Patience;
        for (int tries = 1; ; tries++)
        {
            try { return io(); }
            catch (Exception e) when (Held(e) && DateTime.UtcNow < until)
            {
                if (tries == 1) Console.WriteLine($"[saves] {path} is held by another program ({e.Message}); waiting");
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
public sealed class UnreadableSaveException(string path, Exception? inner)
    : IOException($"{path} can't be read{(inner is null ? "" : $" ({inner.Message})")}; it is left as it is", inner)
{
    public string Path { get; } = path;
}
