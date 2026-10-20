namespace GameServer.Gateway.Tests;

/// <summary>Holds a file open for a moment and lets it go, as an antivirus scan or the search indexer does.</summary>
static class FileHold
{
    /// <summary>Opens <paramref name="path"/> for reading with the given sharing and closes it after
    /// <paramref name="ms"/> milliseconds. FileShare.ReadWrite (no Delete) lets others read but not replace the file;
    /// FileShare.None lets nobody even read it.</summary>
    public static FileStream For(string path, FileShare share, int ms)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
        _ = Task.Delay(ms).ContinueWith(_ => stream.Dispose());
        return stream;
    }
}
