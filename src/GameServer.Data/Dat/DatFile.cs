using System.Text;

namespace GameServer.Data.Dat;

/// <summary>
/// Reader for Path of Exile <c>.datc64</c> tables.
///
/// Layout (verified against SnosMe/Game-dat-viewer and Game-tool-dev/dat-schema v7):
///   u32 rowCount
///   rowCount * rowLength fixed bytes   (rowLength = boundary / rowCount)
///   8 x 0xBB marker, then the variable-data section
/// Fixed fields that reference variable data (strings, arrays) store u32 offsets
/// relative to the start of the variable-data section (which begins at the marker).
/// </summary>
public sealed class DatFile
{
    public const uint Null32 = 0xFEFEFEFE;
    private const byte Magic = 0xBB;
    private const int MagicLength = 8;
    private const int StringTerminator = 4;

    public int RowCount { get; }
    public int RowLength { get; }
    public int FixedLength => _fixed.Length;
    public int VariableLength => _variable.Length;

    private readonly byte[] _fixed;
    private readonly byte[] _variable;

    private DatFile(int rowCount, int rowLength, byte[] fixedData, byte[] variableData)
    {
        RowCount = rowCount;
        RowLength = rowLength;
        _fixed = fixedData;
        _variable = variableData;
    }

    public static DatFile Open(string path) => Parse(File.ReadAllBytes(path));

    public static DatFile Parse(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 12)
            throw new DatFormatException($"file too small ({file.Length} bytes; need >= 12)");

        int rowCount = (int)ReadU32(file, 0);
        int boundary = FindAlignedMagic(file, 4, rowCount);
        if (boundary < 0)
            throw new DatFormatException("variable-data marker (8x 0xBB) not found");
        if (rowCount > 0 && boundary % rowCount != 0)
            throw new DatFormatException($"variable-data marker not row-aligned (boundary={boundary}, rows={rowCount})");

        int rowLength = rowCount > 0 ? boundary / rowCount : 0;

        var fixedData = new byte[boundary];
        Buffer.BlockCopy(file, 4, fixedData, 0, boundary);
        var variableData = new byte[file.Length - 4 - boundary];
        Buffer.BlockCopy(file, 4 + boundary, variableData, 0, variableData.Length);

        return new DatFile(rowCount, rowLength, fixedData, variableData);
    }

    /// <summary>Decode every row of a table using its schema. Throws if the schema and data disagree.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, DatValue>> ReadRows(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (RowCount == 0)
            return Array.Empty<IReadOnlyDictionary<string, DatValue>>();

        var columns = table.Columns;
        var offsets = new int[columns.Count];
        int expected = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            offsets[i] = expected;
            expected += ColumnSize(columns[i]);
        }

        if (expected != RowLength)
            throw new DatFormatException(
                $"schema/row length mismatch for '{table.Name}': schema expects {expected}, data rowLength is {RowLength}");

        var rows = new List<IReadOnlyDictionary<string, DatValue>>(RowCount);
        var keys = new string[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            keys[i] = string.IsNullOrEmpty(columns[i].Name) ? $"__col{i}" : columns[i].Name!;

        for (int r = 0; r < RowCount; r++)
        {
            int rowBase = r * RowLength;
            var dict = new Dictionary<string, DatValue>(columns.Count, StringComparer.Ordinal);
            for (int i = 0; i < columns.Count; i++)
                dict[keys[i]] = ReadValue(_fixed, rowBase + offsets[i], columns[i]);
            rows.Add(dict);
        }
        return rows;
    }

    private DatValue ReadValue(byte[] src, int offset, SchemaColumn c)
    {
        if (c.IsArray)
        {
            uint len = ReadU32(src, offset);
            if (len == 0)
                return DatValue.Array(Array.Empty<DatValue>());

            int varOffset = checked((int)ReadU32(src, offset + 8));

            if (c.Type == DatColumnType.Array)
            {
                // array with unknown element type: preserve length only.
                var nulls = new DatValue[len];
                for (int i = 0; i < len; i++)
                    nulls[i] = DatValue.Null;
                return DatValue.Array(nulls);
            }

            int elementSize = AtomSize(c.Type);
            var items = new DatValue[len];
            for (int i = 0; i < len; i++)
                items[i] = ReadAtom(_variable, checked(varOffset + i * elementSize), c.Type);
            return DatValue.Array(items);
        }

        if (c.IsInterval)
        {
            int elementSize = AtomSize(c.Type);
            var first = ReadAtom(src, offset, c.Type);
            var second = ReadAtom(src, offset + elementSize, c.Type);
            return DatValue.Array(new[] { first, second });
        }

        return ReadAtom(src, offset, c.Type);
    }

    private DatValue ReadAtom(byte[] src, int offset, DatColumnType type)
    {
        switch (type)
        {
            case DatColumnType.Bool:
                return DatValue.Bool(src[offset] != 0);
            case DatColumnType.String:
                return DatValue.Str(ReadString(checked((int)ReadU32(src, offset))));
            case DatColumnType.I16:
                return DatValue.Int(ReadI16(src, offset));
            case DatColumnType.U16:
                return DatValue.UInt(ReadU16(src, offset));
            case DatColumnType.I32:
                return DatValue.Int(ReadI32(src, offset));
            case DatColumnType.U32:
                return DatValue.UInt(ReadU32(src, offset));
            case DatColumnType.F32:
                return DatValue.Float(ReadF32(src, offset));
            case DatColumnType.Row:
            case DatColumnType.EnumRow:
            {
                uint rid = ReadU32(src, offset);
                return DatValue.Row(rid == Null32 ? null : rid);
            }
            case DatColumnType.ForeignRow:
            {
                uint rid = ReadU32(src, offset);
                return DatValue.ForeignRow(rid == Null32 ? null : rid);
            }
            case DatColumnType.Array:
            default:
                return DatValue.Null;
        }
    }

    private static int ColumnSize(SchemaColumn c)
        => c.IsArray ? 16 : AtomSize(c.Type) * (c.IsInterval ? 2 : 1);

    private static int AtomSize(DatColumnType t) => t switch
    {
        DatColumnType.Bool => 1,
        DatColumnType.String => 8,
        DatColumnType.I16 => 2,
        DatColumnType.U16 => 2,
        DatColumnType.I32 => 4,
        DatColumnType.U32 => 4,
        DatColumnType.F32 => 4,
        DatColumnType.Row => 8,
        DatColumnType.EnumRow => 4,
        DatColumnType.ForeignRow => 16,
        DatColumnType.Array => 16,
        _ => 1,
    };

    private string ReadString(int varOffset)
    {
        if (varOffset < 0 || varOffset > _variable.Length)
            throw new DatFormatException($"string offset {varOffset} out of variable bounds ({_variable.Length})");

        int end = FindZeroSequence(_variable, StringTerminator, varOffset);
        if (end < 0)
            throw new DatFormatException($"unterminated string at variable offset {varOffset}");
        while (((end - varOffset) & 1) != 0)
        {
            end = FindZeroSequence(_variable, StringTerminator, end + 1);
            if (end < 0)
                throw new DatFormatException($"unterminated string at variable offset {varOffset}");
        }

        int length = end - varOffset;
        return Encoding.Unicode.GetString(_variable, varOffset, length);
    }

    private static int FindZeroSequence(byte[] data, int count, int from)
    {
        if (from < 0)
            return -1;
        for (int i = from; i + count <= data.Length; i++)
        {
            bool allZero = true;
            for (int j = 0; j < count; j++)
            {
                if (data[i + j] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            if (allZero)
                return i;
        }
        return -1;
    }

    private static int FindAlignedMagic(byte[] file, int start, int rowCount)
    {
        if (file.Length - start < MagicLength)
            return -1;

        for (int i = start; i + MagicLength <= file.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < MagicLength; j++)
            {
                if (file[i + j] != Magic)
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;

            int boundary = i - start;
            if (rowCount == 0 || boundary % rowCount == 0)
                return boundary;
        }
        return -1;
    }

    private static void Ensure(byte[] data, int offset, int size)
    {
        if (offset < 0 || offset + size > data.Length)
            throw new DatFormatException($"read of {size} byte(s) at offset {offset} exceeds section length {data.Length}");
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        Ensure(data, offset, 4);
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static ushort ReadU16(byte[] data, int offset)
    {
        Ensure(data, offset, 2);
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static short ReadI16(byte[] data, int offset) => (short)ReadU16(data, offset);

    private static int ReadI32(byte[] data, int offset) => unchecked((int)ReadU32(data, offset));

    private static float ReadF32(byte[] data, int offset)
        => BitConverter.Int32BitsToSingle(ReadI32(data, offset));
}
