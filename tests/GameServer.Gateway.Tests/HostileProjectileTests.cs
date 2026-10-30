using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A ranged monster's shot flies on the projectile runtime as a server object and lands its hit on the player when it
// arrives, through the monster's own damage pipeline; the monsters themselves are never in its way.
public class HostileProjectileTests
{
    const uint MonsterId = 42;
    static readonly SkillCatalog.MonsterProjectileInfo Mortar = new("Metadata/Projectiles/SandspitterMortarProjectile", 1170, true, 2);
    static readonly SkillCatalog.MonsterProjectileInfo Bolt = new("Metadata/Projectiles/SandSpitter", 1688, false, 2);

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    static byte[] ObjectHeader(ushort id, uint objectId) => new LoginWriter().U16(id).U32(objectId).U32(0).U16(0).ToArray();

    static SpawnedMonster Shooter(int x, int y, SkillCatalog.MonsterProjectileInfo projectile) =>
        new("spitter", new(x, y, 10, width: 300, height: 300), 60, 3, 100)
        {
            Attack = new MonsterAttack(0x102, 0.99, 30, DamagePacket.Of(DamageType.Physical, 10, 10)) { Ranged = true, Projectile = projectile },
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_monster_shot_creates_its_object_and_hits_the_player_when_it_arrives(bool mortar)
    {
        await using var f = new ProjectileRuntimeFixture();
        var m = Shooter(40, 10, mortar ? Mortar : Bolt);
        f.Conn.Monsters.Add(MonsterId, m);
        uint objectId = f.Conn.NextObjectId;
        var use = Program.MonsterProjectileUse(f.Session, MonsterId, m, m.Attack!, m.Attack!.Projectile!, m.ActionGeneration);
        Assert.Equal(2, use.Team);
        f.Session.Schedule(f.Session.Runtime.Now, () => true, () => use);
        f.Advance(0.02);   // released; 30 cells from the player at (10, 10)
        byte[] flight = await f.Read();
        Assert.True(IndexOf(flight, InstanceProtocol.EnterWakeZone(objectId)) >= 0, "the shot is a server object the client draws");
        Assert.Equal(100u, f.Conn.PlayerLife);

        // The hit's presentation reads the connection clock (fortification, flasks), which the live server keeps at or
        // ahead of the simulation; the fixture's simulation runs ahead of the wall clock, so let the clock catch up before
        // the queued presentation is written.
        await f.HoldOutput();
        f.Advance(0.6);    // 1170 or 1688 world units per second cover 30 cells well within this
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        f.ReleaseOutput();
        byte[] arrival = await f.Read();
        Assert.True(f.Conn.PlayerLife < 100, "the hit lands when the shot arrives");
        Assert.True(IndexOf(arrival, ObjectHeader(InstanceProtocol.ActionHitId, MonsterId)) >= 0, "the monster's 0x1af");
        Assert.True(IndexOf(arrival, InstanceProtocol.LifeUpdate(Program.PlayerId, f.Conn.PlayerLife)) >= 0);
        Assert.Equal(100u, m.Life);
    }

    [Fact]
    public async Task A_mortar_lands_where_it_was_aimed_and_misses_a_player_who_left()
    {
        await using var f = new ProjectileRuntimeFixture();
        var m = Shooter(40, 10, Mortar);
        f.Conn.Monsters.Add(MonsterId, m);
        var use = Program.MonsterProjectileUse(f.Session, MonsterId, m, m.Attack!, Mortar, m.ActionGeneration);
        f.Session.Schedule(f.Session.Runtime.Now, () => true, () => use);
        f.Advance(0.02);
        // The player walks out of the splash before it lands (the landing area is MonsterMortarRadiusCells wide).
        f.Conn.Walk.Teleport(10, 10 + (int)Program.MonsterMortarRadiusCells + 5, 0.02);
        f.Advance(0.6);
        await f.Read();
        Assert.Equal(100u, f.Conn.PlayerLife);
        Assert.Empty(f.Session.Runtime.Projectiles);
    }

    // Sand Spitter's mortar effect (GrantedEffects row 617, 3.29.3b tables): the server resolves its projectile and sends
    // the skill's own stats, above all the face-away flag the client needs to turn the Spitter around, without the
    // effect's cast-time entries and with the attack's numbers winning.
    [Fact]
    public void A_mortar_skill_resolves_its_projectile_and_sends_its_own_stats_with_the_attack()
    {
        TestSaves.Ensure();
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1);
        var catalog = Program.SkillsFor(conn);
        var projectile = catalog.MonsterProjectile(617);
        Assert.NotNull(projectile);
        Assert.Equal(Mortar.Metadata, projectile!.Metadata);
        Assert.True(projectile.Lobbed);
        Assert.Equal(1170, projectile.Speed);

        var attack = new MonsterAttack(0x102, 0.99, 30, DamagePacket.Of(DamageType.Physical, 10, 10)) { Ranged = true, Projectile = projectile };
        var stats = Program.MonsterProjectileSkillStats(conn, 617, 1, attack).ToDictionary(s => s.Item1, s => s.Item2);
        uint Key(string name) => Program.Stats.Key(name) ?? throw new Xunit.Sdk.XunitException(name);
        Assert.Equal(1, stats[Key("action_should_face_away_from_target")]);
        Assert.Equal(1, stats[Key("projectile_uses_contact_position")]);
        Assert.Equal(3, stats[Key("projectile_spread_radius")]);
        Assert.Equal(1, stats[Key("skill_is_non_pathing")]);
        Assert.Equal(30, stats[StatKeys.MainHandMaxAttackDistance]);
        Assert.False(stats.ContainsKey(Key("base_spell_cast_time_ms")));
        Assert.False(stats.ContainsKey(Key("mana_cost")));
        Assert.Equal(stats.Count, Program.MonsterProjectileSkillStats(conn, 617, 1, attack).Count);   // no duplicate keys
    }

    // Live 2026-10-29: a mortar landed a few milliseconds of simulation behind the connection clock, after a stats
    // refresh had already read the fortification tracker at the clock's time; the tracker refused the earlier time and
    // the projectile pump closed the connection. The hit lands at the later of the two times.
    [Fact]
    public async Task A_landing_behind_a_later_clock_read_still_commits_its_hit()
    {
        await using var f = new ProjectileRuntimeFixture();
        var m = Shooter(40, 10, Mortar);
        f.Conn.Monsters.Add(MonsterId, m);
        var use = Program.MonsterProjectileUse(f.Session, MonsterId, m, m.Attack!, Mortar, m.ActionGeneration);
        f.Session.Schedule(f.Session.Runtime.Now, () => true, () => use);
        f.Advance(0.02);
        // The connection clock runs on and the tracker is read at its time, as a stats refresh would, before the
        // simulated landing (about 0.3 s of simulation) is resolved.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Program.FortificationOf(f.Conn).Read(f.Conn.Clock.Elapsed.TotalSeconds);
        f.Advance(0.6);
        byte[] arrival = await f.Read();
        Assert.True(f.Conn.PlayerLife < 100);
        Assert.True(IndexOf(arrival, ObjectHeader(InstanceProtocol.ActionHitId, MonsterId)) >= 0);
        Assert.False(f.Conn.Closed);
    }
}
