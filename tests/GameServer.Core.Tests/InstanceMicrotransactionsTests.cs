using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceMicrotransactionsTests
{
    [Fact]
    public void One_record_is_the_bytes_the_client_reads()
    {
        // 23-microtransactions.md §1: u8 +0x08, u32 +0x04 (the MtxTypes HASH32 the panel resolves), u32 +0x00 (the
        // record id), u64 raw +0x10, two wstrs, u8 +0x09, the two variant flags (0), then the three empty lists and the
        // flags byte. Live 2026-10-22 the client read exactly these bytes in this order (trace-mtx-read.js).
        byte[] m = InstanceMicrotransactions.Purchased(
        [
            new InstanceMicrotransactions.Record(MtxTypeHash: 0x12345678, Id: 0x00ABCDEF, Name: "Hi",
                Qword: 0x1122334455667788),
        ]);
        Assert.Equal("01F3" + "01" + "00" + "12345678" + "00ABCDEF" + "8877665544332211" + "00024869" + "0000" +
            "00" + "00" + "00" + "000000" + "01", Convert.ToHexString(m));
    }

    [Fact]
    public void The_add_message_counts_in_four_bytes_and_ends_in_one_flag()
    {
        // 23-microtransactions.md §1: 0x1f4 is the same record list with a u32 count, and one flags byte after it
        // (bit 0 = which list). Live 2026-10-22 this is the message that makes the panel rebuild its rows.
        byte[] one = InstanceMicrotransactions.Added(
        [
            new InstanceMicrotransactions.Record(MtxTypeHash: 0x12345678, Id: 0x00ABCDEF, Name: "Hi",
                Qword: 0x1122334455667788),
        ]);
        Assert.Equal("01F4" + "00000001" + "00" + "12345678" + "00ABCDEF" + "8877665544332211" + "00024869" +
            "0000" + "00" + "00" + "00" + "00", Convert.ToHexString(one));
        // An empty one is "no records": live, a count-0 message replaced a filled list with nothing and rebuilt the
        // panel (which is why the gateway never sends it on its own).
        Assert.Equal("01F4" + "00000000" + "00", Convert.ToHexString(InstanceMicrotransactions.Added([])));
    }

    [Fact]
    public void The_flags_byte_picks_the_list_the_store_reads()
    {
        // 23-microtransactions.md §1: bit 0 of the flags byte selects the panel's records (0) or the store's owned
        // list (1) — live 2026-10-22 this exact message put ALREADY OWNED on the shop's tile for the record's hash.
        // Bits 0 and 1 together (3) fill the store's second list, which no visible page has shown.
        byte[] store = InstanceMicrotransactions.Added(
            [new InstanceMicrotransactions.Record(MtxTypeHash: 0xB63B39AE, Id: 269)], InstanceMicrotransactions.StoreOwned);
        Assert.Equal("01F4" + "00000001" + "00" + "B63B39AE" + "0000010D" + "0000000000000000" + "0000" + "0000" +
            "00" + "00" + "00" + "01", Convert.ToHexString(store));
        Assert.Equal((byte)0, InstanceMicrotransactions.Records);
        Assert.Equal((byte)3, InstanceMicrotransactions.StoreSecond);
        // The guild's list, which the gateway sends empty: count 0 and the flags byte 3 (live 2026-10-22 the client
        // took this exact message and the shop's guild pages then marked nothing).
        Assert.Equal("01F4" + "00000000" + "03",
            Convert.ToHexString(InstanceMicrotransactions.Added([], InstanceMicrotransactions.StoreSecond)));
    }

    [Fact]
    public void An_empty_list_is_the_id_and_four_bytes()
    {
        Assert.Equal("01F3" + "00" + "000000" + "01", Convert.ToHexString(InstanceMicrotransactions.Purchased([])));
    }

    [Fact]
    public void The_list_reads_back()
    {
        List<InstanceMicrotransactions.Record> records =
        [
            new(MtxTypeHash: 0x0DF0_0001, Id: 278, "Black Hole Portal Effect", "A portal effect", 0x0102030405060708, 3, 4),
            new(234032333, 63, "Ébène ✨", ""),
        ];
        List<InstanceMicrotransactions.Equipped> worn = [new(20, 278, 0x0DF0_0001, Option: 2), new(18, 63, 234032333)];
        var read = InstanceMicrotransactions.ReadPurchased(InstanceMicrotransactions.Purchased(records, worn));
        Assert.Equal(records, read.Records);
        Assert.Equal(worn, read.Equipped);
        Assert.Equal(InstanceMicrotransactions.DefaultFlags, read.Flags);
    }

    [Fact]
    public void The_worn_table_follows_the_records_slot_by_slot()
    {
        // Dispatcher case 0x92 resets the 50-slot table at session +0xd118, then fills one 24-byte slot per entry: u8 the
        // MicrotransactionSlot Id, u32 BE the record id, u32 BE the MTXTypeGameSpecific hash, raw u64, u8.
        byte[] m = InstanceMicrotransactions.Purchased([], [new(InstanceMicrotransactions.PortalSlot, 0x0B, 0xB63B39AE)]);
        Assert.Equal("01F3" + "00" + "01" + "14" + "0000000B" + "B63B39AE" + "0000000000000000" + "00" + "0000" + "01",
            Convert.ToHexString(m));
    }

    [Fact]
    public void The_list_request_asks_one_bit_per_list()
    {
        // C→S 0x1f2 (write 0x141f9bd10): live 2026-10-22, 01F201 from the Microtransactions panel and 01F206 (the store's
        // owned list and the hideouts) from the store.
        Assert.Equal(InstanceMicrotransactions.Lists.Records, InstanceMicrotransactions.ReadRequest(Convert.FromHexString("01F201")));
        Assert.Equal(InstanceMicrotransactions.Lists.StoreOwned | InstanceMicrotransactions.Lists.Hideouts,
            InstanceMicrotransactions.ReadRequest(Convert.FromHexString("01F206")));
        Assert.Equal(3, InstanceProtocol.KnownClientLength(InstanceMicrotransactions.RequestId));
    }

    [Fact]
    public void The_hideout_list_is_a_flag_a_compact_count_and_one_byte_per_row()
    {
        Assert.Equal("01F5" + "00" + "00", Convert.ToHexString(InstanceMicrotransactions.Hideouts([])));
        Assert.Equal("01F5" + "01" + "02" + "0305", Convert.ToHexString(InstanceMicrotransactions.Hideouts([3, 5], guild: true)));
    }

    [Fact]
    public void More_records_than_the_byte_count_holds_are_rejected()
    {
        var records = Enumerable.Range(0, 256).Select(i => new InstanceMicrotransactions.Record((uint)i, 0)).ToList();
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceMicrotransactions.Purchased(records));
    }

    [Fact]
    public void A_slot_pick_is_the_slot_the_record_and_an_option()
    {
        // The Cosmetics tab's flyout sends 8 bytes, no length prefix (write 0x141f9be90): u16 id, u8 the
        // MicrotransactionSlot Id (20 = Portal), u32 BE the picked record's id, u8. This is the exact message a click on
        // the Arctic portal sent live 2026-10-22: 01 F9 14 0000000B 00, record 11.
        var pick = InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F9140000000B00"));
        Assert.Equal(InstanceMicrotransactions.PortalSlot, pick.Slot);
        Assert.Equal(11u, pick.RecordId);
        Assert.Equal((byte)0, pick.Option);

        // The client frames it as a fixed 8-byte message, or the read loop stops at it as unknown and loses the rest.
        Assert.Equal(8, InstanceProtocol.KnownClientLength(InstanceMicrotransactions.SlotId));
        Assert.Equal(InstanceMicrotransactions.SlotLength, InstanceProtocol.KnownClientLength(InstanceMicrotransactions.SlotId));
        var framed = InstanceProtocol.SplitClientMessages(Convert.FromHexString("01F9140000000B00"), out _, out byte[] unknown);
        Assert.Empty(unknown);
        var single = Assert.Single(framed);
        Assert.Equal(InstanceMicrotransactions.SlotId, single.Id);
        Assert.Equal("01F9140000000B00", Convert.ToHexString(single.Bytes));
    }

    [Fact]
    public void The_slot_reader_rejects_anything_else()
    {
        Assert.Throws<InvalidDataException>(() =>
            InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F4" + "14000000" + "0B00")));
        Assert.Throws<InvalidDataException>(() => InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F914000000")));
    }

    [Fact]
    public void The_reader_rejects_a_message_that_is_not_0x1f3()
    {
        Assert.Throws<InvalidDataException>(() =>
            InstanceMicrotransactions.ReadPurchased(Convert.FromHexString("01F4" + "00" + "000000" + "01")));
    }
}
