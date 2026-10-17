using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using GameServer.Data.Ggpk;

namespace GgDump;

/// <summary>
/// IOodleDecoder backed by a persistent Node process running ooz-wasm.
/// Covers every Oodle stream type Path of Exile uses (including Selkie),
/// which the managed OozSharp port does not.
/// </summary>
public sealed class NodeOodleDecoder : IOodleDecoder, IDisposable
{
    private readonly Process _process;
    private readonly Stream _toWorker;
    private readonly Stream _fromWorker;
    private readonly object _lock = new();
    private bool _disposed;

    public NodeOodleDecoder(string nodeExe, string workerScript)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(workerScript);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start node worker");
        _toWorker = _process.StandardInput.BaseStream;
        _fromWorker = _process.StandardOutput.BaseStream;
    }

    public byte[] Decompress(ReadOnlyMemory<byte> compressed, int uncompressedSize)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Span<byte> reqHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(reqHeader, (uint)uncompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(reqHeader[4..], (uint)compressed.Length);
            _toWorker.Write(reqHeader);
            _toWorker.Write(compressed.Span);
            _toWorker.Flush();

            Span<byte> respHeader = stackalloc byte[8];
            ReadExactly(_fromWorker, respHeader);
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(respHeader);
            int outLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(respHeader[4..]);

            var payload = new byte[outLen];
            ReadExactly(_fromWorker, payload);

            if (status != 0)
                throw new GgpkFormatException($"ooz worker error: {Encoding.UTF8.GetString(payload)}");

            return payload;
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0)
                throw new EndOfStreamException("ooz worker closed its output");
            read += n;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _toWorker.Dispose(); } catch { /* ignore */ }
        try { _process.WaitForExit(2000); } catch { /* ignore */ }
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        _process.Dispose();
    }
}
