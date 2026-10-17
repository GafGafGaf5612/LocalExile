namespace GameServer.Data.Dat;

/// <summary>Raised when a .datc64 container is malformed or cannot be decoded.</summary>
public sealed class DatFormatException : Exception
{
    public DatFormatException(string message) : base(message) { }
    public DatFormatException(string message, Exception inner) : base(message, inner) { }
}
