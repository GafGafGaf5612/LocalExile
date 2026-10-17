using GameServer.Data.Dat;

namespace GameServer.Data.Tests;

public class DatFileTests
{
    private static readonly SchemaTable TwoColTable = new()
    {
        Name = "Test",
        Columns = new[]
        {
            new SchemaColumn { Name = "Id", Type = DatColumnType.String },
            new SchemaColumn { Name = "Value", Type = DatColumnType.I32 },
        },
    };

    private static void PutU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>Builds a synthetic datc64: [u32 rows][rows][8xBB][variable tail].</summary>
    private static byte[] Build(int rowCount, int rowLength, Action<byte[]> fillRows, byte[] variableTail)
    {
        var rows = new byte[rowCount * rowLength];
        fillRows(rows);

        var variable = new byte[8 + variableTail.Length];
        Array.Fill(variable, (byte)0xBB);
        variableTail.CopyTo(variable, 8);

        var file = new byte[4 + rows.Length + variable.Length];
        PutU32(file, 0, (uint)rowCount);
        rows.CopyTo(file, 4);
        variable.CopyTo(file, 4 + rows.Length);
        return file;
    }

    [Fact]
    public void Decodes_string_and_int_rows()
    {
        // variable tail: "A" (0x41 0x00 + 4 zero terminator) then "BB".
        var tail = new byte[]
        {
            0x41, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x42, 0x00, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        var file = Build(2, 12, rows =>
        {
            PutU32(rows, 0, 8);                       // row0.Id -> var offset 8
            PutU32(rows, 8, unchecked((uint)7));      // row0.Value = 7
            PutU32(rows, 12, 14);                     // row1.Id -> var offset 14
            PutU32(rows, 20, unchecked((uint)(-3)));  // row1.Value = -3
        }, tail);

        var dat = DatFile.Parse(file);
        Assert.Equal(2, dat.RowCount);
        Assert.Equal(12, dat.RowLength);

        var rows = dat.ReadRows(TwoColTable);
        Assert.Equal("A", rows[0]["Id"].Raw);
        Assert.Equal(7L, rows[0]["Value"].Raw);
        Assert.Equal("BB", rows[1]["Id"].Raw);
        Assert.Equal(-3L, rows[1]["Value"].Raw);
    }

    [Fact]
    public void Truncated_variable_section_throws()
    {
        var tail = new byte[]
        {
            0x41, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x42, 0x00, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        var file = Build(2, 12, rows =>
        {
            PutU32(rows, 0, 8);
            PutU32(rows, 8, 7);
            PutU32(rows, 12, 14);
            PutU32(rows, 20, 3);
        }, tail);

        var truncated = file[..^10];
        var dat = DatFile.Parse(truncated);
        Assert.Throws<DatFormatException>(() => dat.ReadRows(TwoColTable));
    }

    [Fact]
    public void Schema_row_length_mismatch_throws()
    {
        var file = Build(1, 12, rows => PutU32(rows, 0, 8), Array.Empty<byte>());
        var dat = DatFile.Parse(file);

        var badTable = new SchemaTable
        {
            Name = "Bad",
            Columns = new[]
            {
                new SchemaColumn { Name = "A", Type = DatColumnType.I32 },
                new SchemaColumn { Name = "B", Type = DatColumnType.I32 },
            },
        };

        Assert.Throws<DatFormatException>(() => dat.ReadRows(badTable));
    }

    [Fact]
    public void Empty_table_returns_empty()
    {
        var file = Build(0, 0, _ => { }, Array.Empty<byte>());
        var dat = DatFile.Parse(file);
        Assert.Equal(0, dat.RowCount);
        Assert.Empty(dat.ReadRows(TwoColTable));
    }

    [Fact]
    public void EnumRow_column_is_four_bytes()
    {
        var table = new SchemaTable
        {
            Name = "E",
            Columns = new[]
            {
                new SchemaColumn { Name = "Kind", Type = DatColumnType.EnumRow },
                new SchemaColumn { Name = "Id", Type = DatColumnType.String },
            },
        };

        var tail = new byte[] { 0x41, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var file = Build(1, 12, rows =>
        {
            PutU32(rows, 0, 9);   // Kind (enum row)
            PutU32(rows, 4, 8);   // Id string offset
        }, tail);

        var dat = DatFile.Parse(file);
        Assert.Equal(12, dat.RowLength);
        var rows = dat.ReadRows(table);
        Assert.Equal(9L, rows[0]["Kind"].AsRow);
        Assert.Equal("A", rows[0]["Id"].Raw);
    }

    [Fact]
    public void Too_small_file_throws()
    {
        Assert.Throws<DatFormatException>(() => DatFile.Parse(new byte[4]));
    }
}
