using System.Buffers.Binary;
using System.Text;

namespace GameServer.Data.Ggpk;

/// <summary>A located file inside a bundle.</summary>
public readonly record struct FileLocation(int BundleIndex, string BundleFile, int Offset, int Size);

/// <summary>
/// The decoded <c>Bundles2/_.index.bin</c> file table.
/// Format per Game-tool-dev Bundle-scheme wiki (see also Game-dat-viewer index-bundle.ts).
/// </summary>
public sealed class GameIndex
{
    private const int FileRecordSize = 20; // u64 hash + i32 bundleIdx + i32 offset + i32 size

    private readonly byte[] _filesInfo;
    private readonly string[] _bundleFiles;
    private readonly Dictionary<ulong, int> _byHash;

    public int FileCount { get; }
    public IReadOnlyList<string> BundleFiles => _bundleFiles;

    private GameIndex(byte[] filesInfo, string[] bundleFiles, int fileCount)
    {
        _filesInfo = filesInfo;
        _bundleFiles = bundleFiles;
        FileCount = fileCount;

        _byHash = new Dictionary<ulong, int>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            ulong h = BinaryPrimitives.ReadUInt64LittleEndian(_filesInfo.AsSpan(i * FileRecordSize, 8));
            _byHash.TryAdd(h, i);
        }
    }

    public static GameIndex Parse(byte[] indexContent)
    {
        ArgumentNullException.ThrowIfNull(indexContent);
        int offset = 0;

        // Bundles.
        int bundlesCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4;

        var bundleFiles = new string[bundlesCount];
        for (int i = 0; i < bundlesCount; i++)
        {
            int nameLen = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
            offset += 4;
            string name = Encoding.UTF8.GetString(indexContent, offset, nameLen);
            offset += nameLen;
            _ = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset)); // decompressed size
            offset += 4;

            bundleFiles[i] = ToBundlePath(name);
        }

        // Files.
        int filesCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4;
        var filesInfo = indexContent.AsSpan(offset, filesCount * FileRecordSize).ToArray();
        offset += filesCount * FileRecordSize;

        // Directories (not needed for file lookup).
        int dirsCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4 + (dirsCount * 20);

        // Remaining bytes are the path representation table; unused here.
        _ = offset;

        return new GameIndex(filesInfo, bundleFiles, filesCount);
    }

    private static string ToBundlePath(string name)
    {
        string file = name.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".bundle.bin";
        return file.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase)
            ? file
            : "Bundles2/" + file;
    }

    /// <summary>Look up a file by its forward-slash path (case-insensitive).</summary>
    public bool TryGetFile(string path, out FileLocation location)
    {
        ulong h = MurmurHash64A.Hash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        if (!_byHash.TryGetValue(h, out int record))
        {
            location = default;
            return false;
        }

        int o = record * FileRecordSize;
        int bundleIdx = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 8));
        int fileOffset = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 12));
        int size = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 16));

        if ((uint)bundleIdx >= (uint)_bundleFiles.Length)
        {
            location = default;
            return false;
        }

        location = new FileLocation(bundleIdx, _bundleFiles[bundleIdx], fileOffset, size);
        return true;
    }
}
