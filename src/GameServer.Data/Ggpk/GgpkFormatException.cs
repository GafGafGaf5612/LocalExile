namespace GameServer.Data.Ggpk;

/// <summary>Raised when a GGPK container, bundle, or index is malformed.</summary>
public sealed class GgpkFormatException : Exception
{
    public GgpkFormatException(string message) : base(message) { }
    public GgpkFormatException(string message, Exception inner) : base(message, inner) { }
}
