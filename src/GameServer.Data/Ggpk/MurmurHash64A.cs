using System.Buffers.Binary;

namespace GameServer.Data.Ggpk;

/// <summary>
/// MurmurHash64A exactly as Path of Exile uses it for bundle index keys
/// (seed 0x1337b33f). Verified against Game-dat-viewer's murmur2.ts.
/// </summary>
public static class MurmurHash64A
{
    private const ulong Seed = 0x1337b33fUL;
    private const ulong M = 0xc6a4a7935bd1e995UL;
    private const int R = 47;

    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        int len = data.Length;
        ulong h = Seed ^ (unchecked((ulong)len) * M);

        int aligned = len & ~7;
        for (int i = 0; i < aligned; i += 8)
        {
            ulong k = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8));
            k *= M;
            k ^= k >> R;
            k *= M;
            h ^= k;
            h *= M;
        }

        int b = aligned;
        switch (len & 7)
        {
            case 7: h ^= (ulong)data[b + 6] << 48; goto case 6;
            case 6: h ^= (ulong)data[b + 5] << 40; goto case 5;
            case 5: h ^= (ulong)data[b + 4] << 32; goto case 4;
            case 4: h ^= (ulong)data[b + 3] << 24; goto case 3;
            case 3: h ^= (ulong)data[b + 2] << 16; goto case 2;
            case 2: h ^= (ulong)data[b + 1] << 8; goto case 1;
            case 1: h ^= data[b]; h *= M; break;
        }

        h ^= h >> R;
        h *= M;
        h ^= h >> R;
        return h;
    }
}
