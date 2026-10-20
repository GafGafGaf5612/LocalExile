using System.Diagnostics;
using System.Text.Json;

namespace GameServer.Core.Saves;

/// <summary>
/// Reads and replaces the server's save files (characters, stashes, the account store, walk-map captures). On Windows
/// another process (an antivirus scan, the search indexer) can hold a file for a moment, so a read or a replace waits for
/// the file to be free. A file that stays unreadable throws instead of counting as missing: the callers would otherwise
/// start over and write over it.
/// </summary>
public static class SaveFiles
{
    /// <summary>How long a read or a replace waits for a held file before it gives up.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>The file's text, or null when there is no such file (or folder). A file that stays held throws
    /// <see cref="UnreadableSaveException"/>.</summary>
    public static string? Read(string path)
    {
        // No File.Exists first: it also says "no" when the file is there but can't be checked, and a save taken for
        // missing is a save started over.
        try { return Retry(path, () => File.ReadAllText(path)); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (Held(e)) { throw Unreadable(path, e, held: true); }
    }

    /// <summary>The saved object, or null when there is no such file. A file that can't be read or isn't one (broken
    /// JSON, "null") throws <see cref="UnreadableSaveException"/> and is left as it is: treating it as missing would
    /// start the character or stash over and write over it.</summary>
    public static T? Load<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (Read(path) is not { } text) return null;
        T? value;
        try { value = JsonSerializer.Deserialize<T>(text, options); }
        catch (JsonException e) { throw Unreadable(path, e, held: false); }
        return value ?? throw Unreadable(path, null, held: false);
    }

    static UnreadableSaveException Unreadable(string path, Exception? e, bool held)
    {
        string message = held
            ? $"{path} is held by another program and can't be read ({e?.Message}); nothing was changed: close what holds it and try again"
            : $"{path} can't be read{(e is null ? "" : $" ({e.Message})")}; it is left as it is: fix it" +
              (File.Exists(path + ".bak") ? $" or put back {path}.bak (the version before its last save)" : "") +
              ". Don't delete it: that starts the character or stash over";
        Console.WriteLine($"[saves] {message}");
        return new UnreadableSaveException(path, message, e);
    }

    /// <summary>
    /// Writes the text to a temporary file next to <paramref name="path"/> (its bytes forced to the disk), keeps the version
    /// it replaces as "&lt;path&gt;.bak" when it can, then renames the temporary file over <paramref name="path"/>. A crash or
    /// power cut at any point leaves <paramref name="path"/> whole, old or new; a write that fails leaves the old file as it
    /// was. Callers that can write the same file from several threads must do so under one lock, so their copies land in
    /// order.
    /// </summary>
    public static void Write(string path, string text)
    {
        // Its own name per write: two writers never take each other's temporary file.
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Retry(tmp, () => { WriteThrough(tmp, text); return 0; });
            Backup(path);
            Retry(path, () => { File.Move(tmp, path, overwrite: true); return 0; });
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // The backup is a help for putting a save back by hand: the save never waits for it or fails because of it.
    static void Backup(string path)
    {
        try { File.Copy(path, path + ".bak", overwrite: true); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (Held(e)) { Console.WriteLine($"[saves] {path}.bak not refreshed ({e.Message}); the save goes ahead"); }
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
        var waited = Stopwatch.StartNew();
        for (int tries = 1; ; tries++)
        {
            try { return io(); }
            catch (Exception e) when (Held(e) && waited.Elapsed < Patience)
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
public sealed class UnreadableSaveException(string path, string message, Exception? inner) : IOException(message, inner)
{
    public string Path { get; } = path;
}
