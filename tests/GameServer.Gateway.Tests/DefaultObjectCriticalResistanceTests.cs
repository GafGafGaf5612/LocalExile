using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class DefaultObjectCriticalResistanceTests
{
    [Theory]
    [InlineData(false, false, false, false, 220)]
    [InlineData(true, false, false, false, 220)]
    [InlineData(false, true, false, false, 330)]
    [InlineData(true, true, false, false, 330)]
    [InlineData(false, true, true, false, 300)]
    [InlineData(true, true, true, false, 300)]
    [InlineData(false, true, true, true, 240)]
    [InlineData(true, true, true, true, 240)]
    public async Task Parsed_C24_default_wand_hit_reads_raw_dev_ignore_independently_of_target_hostility(
        bool hostile, bool critical, bool ignore, bool prohibit, int damage)
    {
        TestSaves.Ensure();
        string character = "DefaultCrit" + Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var writer = new TcpClient();
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "CriticalRoute", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 1, PlayerLife = 100, PlayerSent = true };
            void Set(string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
            Set("global_cannot_crit", critical ? 0 : 1);
            Set(AttackBaseCriticalStrike.WeaponOverrideStat, 10000);
            Set("critical_strikes_ignore_elemental_resistances", ignore ? 1 : 0);
            Assert.Equal(3626u, Program.Stats.Key("critical_strikes_ignore_elemental_resistances"));
            Set("cannot_penetrate_or_ignore_elemental_resistances", prohibit ? 1 : 0);
            Set("base_reduce_enemy_fire_resistance_%", 40);
            Set("base_reduce_enemy_cold_resistance_%", 20);
            Set("attack_minimum_added_fire_damage", 100);
            Set("attack_maximum_added_fire_damage", 100);
            Set("attack_minimum_added_cold_damage", 100);
            Set("attack_maximum_added_cold_damage", 100);
            Set("physical_damage_+%", -100);
            Set("melee_physical_damage_+%", 0);
            Set("attack_speed_+%", 1000);
            PlayerInventories.For(character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
            conn.Pools = Program.PoolsFor(conn, 1) with { Accuracy = int.MaxValue };
            Assert.Equal(critical ? 10000 : 0, conn.Pools.AttackCriticalStrikeChance);
            Assert.Equal(150, conn.Pools.CriticalStrikeMultiplier);
            var variety = Program.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonCasterFire")!;
            var target = new SpawnedMonster("SkeletonCasterFire@68", new(34, 32, 30), 0, 2, 5146)
            { Variety = variety, Level = 68, Hostile = hostile };
            conn.Monsters.Add(42, target);
            conn.Clock.Start();
            var bytes = new LoginWriter().U16(0x24).U32(42).U16(SkillCatalog.FirstSkillId).U16(1).U16(0).ToArray();
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            Assert.Equal((ushort)0x102, use.SkillId);
            var method = typeof(Program).GetMethod("OnUseObject", BindingFlags.NonPublic | BindingFlags.Static)!;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            await ((Task)method.Invoke(null,
                ["default-crit-route-test", writer.GetStream(), ciphers, conn, use, conn.Clock.Elapsed.TotalSeconds])!)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(5146u - (uint)damage, target.Life);
            byte[] received = new byte[reader.Available];
            await reader.GetStream().ReadExactlyAsync(received);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(received);
            Assert.True(received.AsSpan().IndexOf(InstanceProtocol.LifeUpdate(42, target.Life)) >= 0);
        }
        finally { PlayerInventories.Forget(character); }
    }
}
