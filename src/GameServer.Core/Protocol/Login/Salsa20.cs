using System.Buffers.Binary;
using System.Numerics;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Salsa20 stream cipher (256-bit key, 64-bit nonce, 64-bit block counter starting at 0), matching Crypto++'s
/// Salsa20::Encryption as the client uses it: one continuous keystream per direction for the whole connection.
/// Encrypt and decrypt are the same operation.
/// </summary>
public sealed class Salsa20
{
    private readonly uint[] _input = new uint[16];
    private readonly byte[] _block = new byte[64];
    private readonly int _rounds;
    private int _used = 64;

    public Salsa20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, int rounds = 20)
    {
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        if (iv.Length != 8) throw new ArgumentException("iv must be 8 bytes", nameof(iv));
        if (rounds is not (8 or 12 or 20)) throw new ArgumentOutOfRangeException(nameof(rounds));
        _rounds = rounds;
        _input[0] = 0x61707865; _input[5] = 0x3320646e; _input[10] = 0x79622d32; _input[15] = 0x6b206574;
        for (int i = 0; i < 4; i++)
        {
            _input[1 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
            _input[11 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(16 + i * 4)..]);
        }
        _input[6] = BinaryPrimitives.ReadUInt32LittleEndian(iv);
        _input[7] = BinaryPrimitives.ReadUInt32LittleEndian(iv[4..]);
    }

    /// <summary>XORs the keystream into <paramref name="data"/> in place.</summary>
    public void Process(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (_used == 64) NextBlock();
            data[i] ^= _block[_used++];
        }
    }

    private void NextBlock()
    {
        Span<uint> x = stackalloc uint[16];
        _input.CopyTo(x);
        for (int r = 0; r < _rounds; r += 2)
        {
            QR(x, 0, 4, 8, 12); QR(x, 5, 9, 13, 1); QR(x, 10, 14, 2, 6); QR(x, 15, 3, 7, 11);
            QR(x, 0, 1, 2, 3); QR(x, 5, 6, 7, 4); QR(x, 10, 11, 8, 9); QR(x, 15, 12, 13, 14);
        }
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(_block.AsSpan(i * 4), x[i] + _input[i]);
        if (++_input[8] == 0) _input[9]++;
        _used = 0;
    }

    private static void QR(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= BitOperations.RotateLeft(x[a] + x[d], 7);
        x[c] ^= BitOperations.RotateLeft(x[b] + x[a], 9);
        x[d] ^= BitOperations.RotateLeft(x[c] + x[b], 13);
        x[a] ^= BitOperations.RotateLeft(x[d] + x[c], 18);
    }
}
