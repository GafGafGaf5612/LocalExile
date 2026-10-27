using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class SkillCosmeticBindingTests
{
    const string A = "Metadata/Items/Gems/SkillGemSpark";
    const string B = "Metadata/Items/Gems/SkillGemKineticBlast";
    const uint SparkRecord = 122;

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public PlayerInventories Items => PlayerInventories.For(Conn.Character!, Program.Items);
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));

        public Fixture()
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "CosmeticTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "Mtx" + Guid.NewGuid().ToString("N"), ClassRow = 1, WornCosmetics = [],
                PlayerLife = 100, PlayerMana = 100 };
            Conn.Pools = Program.PoolsFor(Conn, 100);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        static T Call<T>(string method, params object?[] args) => (T)typeof(Program)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        public List<InstanceMicrotransactions.SkillSlot> Slots() => Call<List<InstanceMicrotransactions.SkillSlot>>("SkillSlots", Conn);
        public Task Refresh() => Call<Task>("SendWornCosmetics", "cosmetic-test", writer.GetStream(), Ciphers, Conn, "test");
        public Task Pick(uint key, uint record) => Call<Task>("PickSkillCosmetic", "cosmetic-test", writer.GetStream(), Ciphers, Conn, (key, record));
        public void Equip(params string[] gems)
        {
            foreach (var item in Items.Items(PlayerInventories.Weapon1)) Items.Remove(PlayerInventories.Weapon1, item.Id);
            if (gems.Length == 0) return;
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            { Sockets = [.. gems.Select(g => new ItemSocket(SocketColour.Blue, ItemCatalog.Create(Program.Items.Find(g)!)))] };
            Items.Add(PlayerInventories.Weapon1, 0, 0, host);
        }
        public uint Hash(string gem) => Program.Items.Find(gem)!.Hash;
        public uint Saved(string gem, byte slotType = 0) => CharacterSaves.SkillCosmetics(Conn.Character!)
            .GetValueOrDefault(CharacterSaves.SkillCosmeticKey(Hash(gem), slotType))?.Record ?? 0;
        public uint Record(string gem, byte slotType = 0) => Program.Mtx.Owned.First(r =>
            Program.MtxEffects.FitsSkill(r.MtxTypeHash, Program.Items.Find(gem)!, slotType)).Id;
        public int Variation(string gem) => Call<int>("SkillArtVariation", Conn, Hash(gem));
        public async Task AssertPackets(params byte[][] packets)
        {
            byte[] expected = [.. packets.SelectMany(p => p)];
            byte[] actual = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(actual, timeout.Token);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(actual);
            Assert.Equal(expected, actual);
            Assert.Equal(0, reader.Available);
        }
        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop(); PlayerInventories.Forget(Conn.Character!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_retires_A_key_even_after_clearing_its_MTX(bool clear)
    {
        using var f = new Fixture();
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        await f.Pick(a.Key, SparkRecord);
        if (clear) await f.Pick(a.Key, 0);
        f.Equip(B);
        var b = Assert.Single(f.Slots());
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(f.Hash(B), b.GemHash);
        Assert.Equal(0u, b.RecordId);
        Assert.False(f.Conn.SkillSlots.ContainsKey(a.Key));
        await f.Pick(a.Key, SparkRecord); // delayed picker from the removed skill
        await f.Pick(b.Key, 0);
        Assert.Equal(clear ? 0u : SparkRecord, f.Saved(A));
        Assert.Equal(0u, f.Saved(B));
        f.Equip(A);
        var restored = Assert.Single(f.Slots());
        Assert.NotEqual(a.Key, restored.Key);
        Assert.NotEqual(b.Key, restored.Key);
        Assert.Equal(clear ? 0u : SparkRecord, restored.RecordId);
    }

    [Fact]
    public void Unchanged_targets_keep_keys_but_removal_reappearance_and_socket_changes_do_not()
    {
        using var f = new Fixture();
        f.Equip(A, B);
        var original = f.Slots();
        Assert.Equal(original, f.Slots());
        f.Equip(B); // B moves from socket 1 to socket 0
        var moved = Assert.Single(f.Slots());
        Assert.DoesNotContain(original, s => s.Key == moved.Key);
        f.Equip();
        Assert.Empty(f.Slots());
        f.Equip(A, B);
        var returned = f.Slots();
        Assert.All(returned, s => Assert.DoesNotContain(original.Append(moved), old => old.Key == s.Key));
    }

    [Fact]
    public async Task A_pick_for_a_removed_target_is_ignored_before_the_next_cosmetic_refresh()
    {
        using var f = new Fixture();
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        CharacterSaves.SetSkillCosmetic(f.Conn.Character!, a.GemHash, a.SlotType, new(SparkRecord));
        f.Equip(B);
        await f.Pick(a.Key, 0);
        Assert.Equal(SparkRecord, f.Saved(A));
        Assert.Equal(0u, f.Saved(B));
    }

    [Fact]
    public async Task A_record_from_the_old_picker_cannot_be_saved_on_the_replacement_gem()
    {
        using var f = new Fixture();
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        await f.Pick(a.Key, SparkRecord);
        f.Equip(B);
        var b = Assert.Single(f.Slots());
        await f.Pick(b.Key, SparkRecord);
        Assert.Equal(SparkRecord, f.Saved(A));
        Assert.Equal(0u, f.Saved(B));
    }

    [Theory]
    [InlineData(A, B)]
    [InlineData("Metadata/Items/Gems/SkillGemFireball", "Metadata/Items/Gems/SkillGemIceNova")]
    [InlineData("Metadata/Items/Gems/SkillGemRighteousFire", B)]
    public async Task Each_gem_keeps_its_own_effect_across_swaps_and_clearing_B(string first, string second)
    {
        using var f = new Fixture();
        f.Equip(first);
        var a = Assert.Single(f.Slots());
        uint aRecord = f.Record(first), bRecord = f.Record(second);
        await f.Pick(a.Key, aRecord);
        int aVariation = f.Variation(first);
        f.Equip(second);
        var b = Assert.Single(f.Slots());
        await f.Pick(b.Key, bRecord);
        Assert.Equal(aRecord, f.Saved(first));
        Assert.Equal(bRecord, f.Saved(second));
        f.Equip(first);
        Assert.Equal(aRecord, Assert.Single(f.Slots()).RecordId);
        Assert.Equal(aVariation, f.Variation(first)); // includes the art input used by RF ring/buff restoration
        f.Equip(second);
        b = Assert.Single(f.Slots());
        Assert.Equal(bRecord, b.RecordId);
        await f.Pick(b.Key, 0);
        Assert.Equal(aRecord, f.Saved(first));
        Assert.Equal(0u, f.Saved(second));
        Assert.Equal(0, f.Variation(second));
    }

    [Fact]
    public async Task Primary_and_totem_skin_picks_restore_and_clear_independently()
    {
        using var f = new Fixture();
        const string totem = "Metadata/Items/Gems/SkillGemSearingBond";
        f.Equip(totem);
        var slots = f.Slots();
        var primary = slots.Single(s => s.SlotType == 0);
        var skin = slots.Single(s => s.SlotType == 1);
        uint primaryRecord = f.Record(totem), skinRecord = f.Record(totem, 1);
        await f.Pick(primary.Key, primaryRecord);
        await f.Pick(skin.Key, skinRecord);
        await f.Pick(primary.Key, skinRecord); // right gem, wrong effect slot
        Assert.Equal(primaryRecord, f.Saved(totem));
        f.Equip(B);
        f.Slots();
        f.Equip(totem);
        slots = f.Slots();
        primary = slots.Single(s => s.SlotType == 0);
        skin = slots.Single(s => s.SlotType == 1);
        Assert.Equal(primaryRecord, primary.RecordId);
        Assert.Equal(skinRecord, skin.RecordId);
        await f.Pick(skin.Key, 0);
        Assert.Equal(primaryRecord, f.Saved(totem));
        Assert.Equal(0u, f.Saved(totem, 1));
    }

    [Fact]
    public void Additional_skills_and_effect_slot_types_do_not_change_an_existing_target_key()
    {
        using var f = new Fixture();
        const string totem = "Metadata/Items/Gems/SkillGemSearingBond";
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        f.Equip(A, totem, A); // duplicate gems still share a per-type saved choice
        var expanded = f.Slots();
        Assert.Equal(3, expanded.Count);
        Assert.Equal(a, expanded.Single(s => s.GemHash == a.GemHash));
        Assert.Equal(new byte[] { 0, 1 }, expanded.Where(s => s.GemHash == f.Hash(totem)).Select(s => s.SlotType));
        Assert.Equal(3, expanded.Select(s => s.Key).Distinct().Count());
        f.Equip(A);
        Assert.Equal(a, Assert.Single(f.Slots()));
    }

    [Fact]
    public async Task Queued_refresh_samples_current_targets_and_publishes_only_at_the_send_gate()
    {
        using var f = new Fixture();
        f.Equip(A);
        var a = Assert.Single(f.Slots());
        await f.Gate.WaitAsync();
        Task first, second;
        try
        {
            first = f.Refresh(); // blocked while A is equipped
            f.Equip(B);
            second = f.Refresh();
            Assert.Equal(a, f.Conn.SkillSlots[a.Key]);
        }
        finally { f.Gate.Release(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var b = Assert.Single(f.Slots());
        Assert.NotEqual(a.Key, b.Key);
        Assert.False(f.Conn.SkillSlots.ContainsKey(a.Key));
        var packet = InstanceMicrotransactions.Purchased([], [], [b]);
        await f.AssertPackets(packet, packet);
    }
}
