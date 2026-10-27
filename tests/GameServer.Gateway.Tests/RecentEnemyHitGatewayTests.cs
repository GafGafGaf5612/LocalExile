using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class RecentEnemyHitGatewayTests
{
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    sealed class TraceRandom(int seed = 123, int? armourRoll = null, double? blockRoll = null) : Random(seed)
    {
        public readonly List<string> Draws = [];
        public Action? BeforeDouble;
        public override int Next(int maxValue)
        {
            Draws.Add($"next:{maxValue}");
            return maxValue == 100 && armourRoll is int roll ? roll : base.Next(maxValue);
        }
        public override int Next(int minValue, int maxValue)
        {
            Draws.Add($"range:{minValue}:{maxValue}");
            return base.Next(minValue, maxValue);
        }
        public override double NextDouble()
        {
            Draws.Add("double"); BeforeDouble?.Invoke(); return blockRoll ?? base.NextDouble();
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers receiver = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly ClientState Conn;
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Fixture(int armour = 500, string? character = null, InstanceState? instance = null)
        {
            TestSaves.Ensure();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Conn = new(new(ushort.MaxValue, "IncomingArmourTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = character ?? "IncomingArmour" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                Instance = instance ?? new(),
                PlayerLife = 10000, PlayerMana = 50, PlayerSent = true,
                Pools = new(1, 10000, 1000, 0, 0, Armour: armour,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            };
            Conn.Commands = (ChatCommands)Method("Commands").Invoke(null, ["incoming-test", Stream, Ciphers, Conn])!;
            Set(RecentEnemyHits.PerHit, 10);
        }
        public void Set(string name, int value) => Conn.PlayerStats[Key(name)] = value;
        public int Count => Conn.RecentEnemyHits.Count(Conn.Clock.Elapsed.TotalSeconds);
        public int Chance => Program.CharacterPlayerStats(Conn)[Key(RecentEnemyHits.VirtualChance)];
        public Task Command(string text)
        {
            byte[] bytes = new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(bytes, out var carry, out var unknown);
            Assert.Empty(carry); Assert.Empty(unknown); Assert.Single(messages);
            return Program.OnClientMessages("incoming-test", Stream, Ciphers, messages, unknown, Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        public SpawnedMonster Monster(DamagePacket? hit = null, double seconds = .002, bool hostile = true,
            MonsterModSet? mods = null)
        {
            var monster = new SpawnedMonster("incoming target", new(34, 32, 30), 0, 2, 1000)
            { Hostile = hostile, Mods = mods ?? MonsterModSet.None, Attack = new(SkillCatalog.FirstSkillId, seconds, 20,
                hit ?? DamagePacket.Of(DamageType.Physical, 100, 100)) };
            Conn.Monsters[42] = monster;
            return monster;
        }
        public Task Swing(SpawnedMonster monster)
        {
            lock (Conn.Monsters) return (Task)Method("MonsterSwing").Invoke(null,
                ["incoming-test", Stream, Ciphers, Conn, 42u, monster, (34, 32), (32, 32), Conn.IncomingHitEpoch])!;
        }
        public Task Present() => Program.SendRecentEnemyHits("incoming-test", Stream, Ciphers, Conn);
        public async Task<byte[]> Read()
        {
            var bytes = new byte[reader.Available];
            if (bytes.Length > 0) await reader.GetStream().ReadExactlyAsync(bytes);
            receiver.Receive.Process(bytes);
            return bytes;
        }
        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    [Fact]
    public async Task Parsed_enemy_contacts_use_prior_history_and_leave_base_armour_and_attack_bonus_unchanged()
    {
        using var f = new Fixture();
        var random = new TraceRandom(armourRoll: 0); f.Conn.IncomingHitRandom = random;
        f.Set(ArmourAttackDamage.Per450Armour, 2);
        foreach (var (chance, damage) in new[] { (0, 50), (10, 33), (20, 33), (30, 33), (30, 33) })
        {
            Assert.Equal(chance, f.Chance);
            uint before = f.Conn.PlayerLife;
            await f.Command("/hit physical 100");
            Assert.Equal((uint)damage, before - f.Conn.PlayerLife);
            Assert.Equal(500, f.Conn.Pools.Armour);
            Assert.Equal(2, Program.CharacterPlayerStats(f.Conn)[Key("attack_damage_+%")]);
        }
        Assert.Equal(5, f.Count);
        Assert.Equal(4, random.Draws.Count);
        Assert.All(random.Draws, d => Assert.Equal("next:100", d));
        Assert.Equal(double.NegativeInfinity, f.Conn.LastEnemyHitAt);
    }

    [Theory]
    [InlineData("/hit physical 100", 29, 0, 33, 1)]
    [InlineData("/hit physical 100", 30, 0, 50, 1)]
    [InlineData("/spellhit physical 100", 29, 20, 13, 2)]
    [InlineData("/blockhit attack physical 100", 30, 20, 30, 2)]
    [InlineData("/blockhit spell physical 100", 29, 20, 13, 2)]
    public async Task Parsed_routes_use_one_decision_and_exact_diagnostics(
        string command, int roll, int reduction, int damage, int draws)
    {
        using var f = new Fixture();
        for (int i = 0; i < 3; i++) f.Conn.RecentEnemyHits.Record(f.Conn.Clock.Elapsed.TotalSeconds);
        f.Set("base_additional_physical_damage_reduction_%", reduction);
        var random = new TraceRandom(armourRoll: roll, blockRoll: .99); f.Conn.IncomingHitRandom = random;
        await f.Command(command);
        Assert.Equal(10000u - (uint)damage, f.Conn.PlayerLife);
        Assert.Equal(4, f.Count);
        Assert.Equal(draws, random.Draws.Count);
        string text = System.Text.Encoding.ASCII.GetString(await f.Read());
        // Replies use the exact decision used for damage; reading diagnostics cannot reroll.
        if (command.StartsWith("/hit")) Assert.Contains(roll == 29 ? "67% armour" : "50% armour", text);
    }

    [Theory]
    [InlineData("/hit physical 100")]
    [InlineData("/spellhit physical 100")]
    [InlineData("/blockhit attack physical 100")]
    [InlineData("/blockhit spell physical 100")]
    public async Task Zero_damage_immunity_counts_without_armour_draw_and_block_counts_exactly_once(string command)
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        f.Set("physical_immunity", 1);
        var random = new TraceRandom(armourRoll: 0, blockRoll: 0); f.Conn.IncomingHitRandom = random;
        await f.Command(command);
        Assert.Equal(10000u, f.Conn.PlayerLife);
        Assert.Equal(1, f.Count);
        Assert.Equal(command.StartsWith("/hit") ? 0 : 1, random.Draws.Count);
        f.Set("physical_immunity", 0);
        f.Conn.Pools = f.Conn.Pools with { BlockChance = 100, SpellBlockChance = 100 };
        if (command.StartsWith("/blockhit"))
        {
            random.Draws.Clear(); await f.Command(command);
            Assert.Equal(10000u, f.Conn.PlayerLife); Assert.Equal(2, f.Count);
            Assert.Equal(new[] { "double" }, random.Draws);
        }
    }

    [Fact]
    public async Task Count_runs_without_modifier_and_development_outputs_cannot_forge_combat_or_presentation()
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.PerHit, 0);
        await f.Command("/hit fire 1"); await f.Command("/spellhit fire 1");
        Assert.Equal(2, f.Count); Assert.Equal(0, f.Chance);
        await f.Command($"/set_stat {RecentEnemyHits.Counter} 999");
        await f.Command($"/set_stat {RecentEnemyHits.VirtualChance} 100");
        Assert.Equal(2, Program.CharacterPlayerStats(f.Conn)[Key(RecentEnemyHits.Counter)]);
        Assert.Equal(0, f.Chance);
        f.Set(RecentEnemyHits.PerHit, 10); Assert.Equal(20, f.Chance);
        f.Set(RecentEnemyHits.GenericChance, 50); Assert.Equal(70, f.Chance);
        f.Set(RecentEnemyHits.PerHit, 0); Assert.Equal(50, f.Chance);
        f.Set(RecentEnemyHits.GenericChance, 0); Assert.Equal(0, f.Chance);
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("paused")]
    public async Task Parsed_development_guard_precedes_any_count_pool_charge_or_rng_mutation(string refusal)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        if (refusal == "dead") f.Conn.PlayerLife = 0;
        if (refusal == "closed") f.Conn.Closed = true;
        if (refusal == "leaving") f.Conn.LeavingArea = true;
        if (refusal == "paused") f.Conn.Clock.Stop();
        uint life = f.Conn.PlayerLife, mana = f.Conn.PlayerMana;
        foreach (string command in new[] { "/hit physical 100", "/spellhit physical 100", "/blockhit attack physical 100" })
            await f.Command(command);
        Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
        Assert.Equal(life, f.Conn.PlayerLife); Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.False(((ChargeSession)Method("ChargesOf").Invoke(null, [f.Conn])!).Pending);
        Assert.Empty(f.Conn.SkillReadyAt);
    }

    [Fact]
    public void Self_hit_can_benefit_without_counting_and_dot_uses_resistance_only()
    {
        using var f = new Fixture();
        f.Set(RecentEnemyHits.GenericChance, 100);
        Assert.Equal(33, (int)Method("MitigateTaken").Invoke(null, [f.Conn, DamageType.Physical, 100, 0, 75])!);
        Assert.Equal(0, f.Count);
        var dot = new DamageOverTime(DamageType.Physical, 100, 1);
        Assert.Equal(100, dot.Tick(1, 0).Damage);
        Assert.Equal(0, f.Count);
    }

    [Theory]
    [InlineData("/hit physical 0")]
    [InlineData("/spellhit physical -1")]
    [InlineData("/blockhit self physical 100")]
    [InlineData("/blockhit spell unknown 100")]
    public async Task Invalid_parsed_contact_does_not_spend_or_record(string command)
    {
        using var f = new Fixture();
        var random = new TraceRandom(); f.Conn.IncomingHitRandom = random;
        await f.Command(command);
        Assert.Equal(0, f.Count); Assert.Empty(random.Draws);
        Assert.Equal(10000u, f.Conn.PlayerLife); Assert.Equal(50u, f.Conn.PlayerMana);
        Assert.False(((ChargeSession)Method("ChargesOf").Invoke(null, [f.Conn])!).Pending);
    }
}
