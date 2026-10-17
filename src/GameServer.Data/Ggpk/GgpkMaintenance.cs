using LibGGPK3;
using LibGGPK3.Records;
using System.Runtime.Intrinsics;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Small direct operations on a <c>Content.ggpk</c> for running a private server. Never use these while the game client
/// is running: the client keeps the GGPK open.
/// </summary>
public static class GgpkMaintenance
{
    /// <summary>
    /// SHA-256 hash stored on the root directory record. The patch client compares it with the hash in the patch server's
    /// first reply; if they are equal it logs "No patching required" and skips patching (client 0x1428ead70).
    /// </summary>
    public static byte[] RootHash(string ggpkPath)
    {
        // Read-only and share-friendly, so it also works while another process has the file open (if it allows readers).
        using var stream = new FileStream(ggpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ggpk = new GGPK(stream, false);
        return HashBytes(ggpk.Root);
    }

    public static byte[]? TryReadFile(string ggpkPath, string path)
    {
        using var ggpk = new GGPK(ggpkPath);
        return ggpk.Root.TryFindNode(path, out var node) && node is FileRecord fr ? fr.Read() : null;
    }

    /// <summary>Replaces an existing file in place (LibGGPK3 also updates the file and directory hashes up to the root).</summary>
    public static void ReplaceFile(string ggpkPath, string path, ReadOnlySpan<byte> data)
    {
        using var ggpk = new GGPK(ggpkPath);
        if (!ggpk.Root.TryFindNode(path, out var node) || node is not FileRecord fr)
            throw new FileNotFoundException($"file not found in GGPK: {path}", path);
        fr.Write(data);
    }

    private static byte[] HashBytes(TreeNode node)
    {
        byte[] b = new byte[32];
        node.Hash.CopyTo(b);
        return b;
    }
}
