namespace GameServer.Data.Dat;

public enum DatValueKind
{
    Null,
    Bool,
    Int,
    UInt,
    Float,
    String,
    Row,
    ForeignRow,
    Array,
}

/// <summary>
/// A single decoded value from a .datc64 row. <see cref="Raw"/> is the boxed
/// CLR value: <c>bool</c>, <c>long</c>, <c>ulong</c>, <c>double</c>, <c>string</c>,
/// or <c>DatValue[]</c> for arrays.
/// </summary>
public readonly struct DatValue
{
    public DatValueKind Kind { get; }
    public object? Raw { get; }

    private DatValue(DatValueKind kind, object? raw)
    {
        Kind = kind;
        Raw = raw;
    }

    public static readonly DatValue Null = new(DatValueKind.Null, null);

    public static DatValue Bool(bool v) => new(DatValueKind.Bool, v);
    public static DatValue Int(long v) => new(DatValueKind.Int, v);
    public static DatValue UInt(ulong v) => new(DatValueKind.UInt, v);
    public static DatValue Float(double v) => new(DatValueKind.Float, v);
    public static DatValue Str(string v) => new(DatValueKind.String, v);
    public static DatValue Row(long? rid) => rid is null ? Null : new(DatValueKind.Row, rid.Value);
    public static DatValue ForeignRow(long? rid) => rid is null ? Null : new(DatValueKind.ForeignRow, rid.Value);
    public static DatValue Array(DatValue[] items) => new(DatValueKind.Array, items);

    public bool IsNull => Kind == DatValueKind.Null;

    /// <summary>Row/foreign-row index, or null for the 0xFEFEFEFE sentinel.</summary>
    public long? AsRow => Kind is DatValueKind.Row or DatValueKind.ForeignRow ? (long?)Raw : null;

    public DatValue[] AsArray => Kind == DatValueKind.Array ? (DatValue[])Raw! : System.Array.Empty<DatValue>();

    public override string ToString() => Raw switch
    {
        null => "null",
        DatValue[] a => $"[{a.Length}]",
        _ => Raw.ToString() ?? "null",
    };

    /// <summary>Project into plain CLR values (null/bool/long/ulong/double/string/object?[]) for JSON.</summary>
    public object? ToClr() => Kind switch
    {
        DatValueKind.Array => ((DatValue[])Raw!).Select(v => v.ToClr()).ToArray(),
        DatValueKind.Null => null,
        _ => Raw,
    };
}
