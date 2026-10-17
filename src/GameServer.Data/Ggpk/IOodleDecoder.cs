namespace GameServer.Data.Ggpk;

/// <summary>Decompresses one Oodle-compressed chunk to exactly <paramref name="uncompressedSize"/> bytes.</summary>
public interface IOodleDecoder
{
    byte[] Decompress(ReadOnlyMemory<byte> compressed, int uncompressedSize);
}

/// <summary>
/// In-process decoder backed by OozSharp. Supports Kraken and Mermaid.
/// For Selkie/Leviathan streams use the Node-backed decoder in the extractor.
/// </summary>
public sealed class ManagedOodleDecoder : IOodleDecoder
{
    private readonly object _lock = new();
    private readonly OozSharp.Kraken _kraken = new();

    public byte[] Decompress(ReadOnlyMemory<byte> compressed, int uncompressedSize)
    {
        lock (_lock)
        {
            return _kraken.Decompress(compressed.Span, uncompressedSize).ToArray();
        }
    }
}
