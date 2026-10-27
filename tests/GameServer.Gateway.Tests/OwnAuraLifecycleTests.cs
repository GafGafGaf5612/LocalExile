using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Queued_Rage_snapshot_reconciles_owned_aura_pairs_before_stats_and_is_idempotent(
        bool rageChanged, bool paused)
    {
        using var f = new Fixture("Haste");
        f.Conn.PlayerStats[Program.Stats.Key("no_inherent_rage_loss")!.Value] = 1;
        await f.Use("instant-object");
        var aura = Assert.Single(f.Conn.Auras).Value;
        Assert.Single(f.Conn.PresentedAuras);
        var player = Program.CharacterPlayerStats(f.Conn);
        var inputs = RageStats.Inputs(name => Program.Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0);
        Assert.True(f.Conn.Rage.Gain(3, f.Conn.Clock.Elapsed.TotalSeconds, inputs));
        await Program.SendRage("own-aura-rage-test", f.Stream, f.Ciphers, f.Conn);
        ushort previousBuff = f.Conn.RageBuff, next = f.Conn.NextBuff;
        Assert.NotEqual((ushort)0, previousBuff);
        await f.Drain();
        if (paused) f.Conn.Clock.Stop();
        if (rageChanged) Assert.True(f.Conn.Rage.Gain(4, f.Conn.Clock.Elapsed.TotalSeconds, inputs));
        int current = rageChanged ? 7 : 3;
        f.Conn.PlayerMana = 13;
        await f.Gate.WaitAsync();
        Task pending = Program.SendRage("own-aura-rage-test", f.Stream, f.Ciphers, f.Conn);
        try
        {
            Assert.False(pending.IsCompleted);
            lock (f.Conn.Monsters) f.Conn.PlayerStats[21148] = 1;
        }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] bytes = await f.Drain();
        Assert.Empty(f.Conn.Auras);
        Assert.Empty(f.Conn.PresentedAuras);
        byte[] ends = [.. AuraCatalog.EndPackets(aura, 0x1000).SelectMany(p => p)];
        Assert.True(bytes.AsSpan().StartsWith(ends));
        foreach (var packet in AuraCatalog.EndPackets(aura, 0x1000)) Assert.Equal(1, Count(bytes, packet));
        Assert.Equal(13u, f.Conn.PlayerMana);
        Assert.Contains((13u, 0u), ManaPackets(bytes));
        Assert.Equal(0, f.Conn.Pools.MovementVelocityPct);
        Assert.Equal(new RageState.Value(current, 30, current), f.Conn.SentRage);
        Assert.Equal(paused, f.Conn.Paused);
        Assert.Equal((ushort)(next + (rageChanged ? 1 : 0)), f.Conn.NextBuff);
        Assert.Equal(rageChanged ? 1 : 0, Count(bytes, InstanceEntities.RemoveBuff(0x1000, previousBuff)));
        if (rageChanged)
        {
            Assert.NotEqual(previousBuff, f.Conn.RageBuff);
            Assert.Equal(1, Count(bytes, InstanceEntities.AddBuff(0x1000, f.Conn.RageBuff, 1060,
                0x1000, stacks: (ushort)current)));
        }
        else Assert.Equal(previousBuff, f.Conn.RageBuff);
        var skills = Skills(f.Conn);
        Assert.Contains((11075u, 1), skills.Single(s => s.Id == f.Skill()).Stats!);
        Assert.Contains((15798u, 1), skills.Single(s => s.Id == f.Skill()).Stats!);
        int actorAt = bytes.AsSpan().IndexOf(InstanceEntities.PlayerSkillsChange(0x1000, skills, []));
        int statsAt = bytes.AsSpan().IndexOf(InstanceEntities.StatsUpdate(0x1000, [])[..12]);
        Assert.Equal(ends.Length, statsAt);
        Assert.True(actorAt > statsAt + 12);
        // Check the first actual player snapshot, including explicit clears absent from a later rebuilt dictionary.
        foreach (var (key, value) in new[] { (11075u, 1), (21148u, 1), (StatKeys.MovementVelocityPct, 0),
            (Program.Stats.Key("cast_speed_+%")!.Value, 0), (Program.Stats.Key("current_rage")!.Value, current) })
        {
            var encoded = new LoginWriter();
            InstanceProtocol.CompactUInt(encoded, key);
            InstanceProtocol.CompactInt(encoded, value);
            Assert.True(bytes.AsSpan(statsAt + 12, actorAt - statsAt - 12).IndexOf(encoded.ToArray()) >= 0);
        }
        Assert.Equal(new[] { "Haste" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
        ushort buff = f.Conn.RageBuff;
        next = f.Conn.NextBuff;
        await Program.SendRage("own-aura-rage-test", f.Stream, f.Ciphers, f.Conn);
        Assert.Empty(await f.Drain());
        Assert.Equal(buff, f.Conn.RageBuff);
        Assert.Equal(next, f.Conn.NextBuff);
        Assert.Equal(new RageState.Value(current, 30, current), f.Conn.SentRage);
    }

    [Theory]
    [InlineData("ground")]
    [InlineData("self")]
    public async Task Queued_ordinary_aura_acknowledgement_rechecks_the_caster_before_native_use(string route)
    {
        using var f = new Fixture("Haste");
        uint mana = f.Conn.PlayerMana;
        ushort next = f.Conn.NextBuff;
        await f.Gate.WaitAsync();
        Task pending = f.Use(route);
        Assert.Equal(18, f.Conn.ActionCount); // Initial admission passed; the action is still the same owner.
        var cooldown = Assert.Single(f.Conn.SingleUseCooldowns).Value;
        Assert.Equal((ushort)0, cooldown.NativeSkillId);
        try { lock (f.Conn.Monsters) f.Conn.PlayerStats[21148] = 1; }
        finally { f.Gate.Release(); }
        await pending;
        byte[] bytes = await f.Drain();
        Assert.Equal(0, Count(bytes, [0x01, 0xab, 0, 0, 0x10, 0]));
        Assert.Equal((ushort)0, cooldown.NativeSkillId);
        AssertNoAdds(bytes);
        Assert.Empty(f.Conn.Auras);
        Assert.Empty(CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras ?? []);
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(next, f.Conn.NextBuff);
        Assert.Equal(18, f.Conn.ActionCount);
    }

    [Theory]
    [InlineData("ground", 0, 0)]
    [InlineData("self", 0, 0)]
    [InlineData("ground", 1, 1)]
    [InlineData("self", 1, 1)]
    public async Task Queued_duplicate_curse_echo_checks_only_the_requested_instances_current_supports(
        string route, int index, int acknowledgements)
    {
        using var f = new Fixture("Enfeeble");
        AddBlasphemyAndUnlinkedDuplicate(f);
        await f.Gate.WaitAsync();
        Task pending = f.Use(route, index);
        Assert.Equal(18, f.Conn.ActionCount);
        try { lock (f.Conn.Monsters) f.Conn.PlayerStats[21148] = 1; }
        finally { f.Gate.Release(); }
        await pending;
        byte[] bytes = await f.Drain();
        Assert.Equal(acknowledgements, Count(bytes, [0x01, 0xab, 0, 0, 0x10, 0]));
        Assert.Empty(f.Conn.Auras); // No curse executor is added by accepting the unlinked spell's input.
    }

    [Theory]
    [InlineData("ground", false)]
    [InlineData("self", false)]
    [InlineData("ground", true)]
    [InlineData("self", true)]
    public async Task Ordinary_aura_echo_keeps_the_original_action_and_connection_ownership_guard(
        string route, bool retired)
    {
        using var f = new Fixture("Haste");
        await f.Gate.WaitAsync();
        Task pending = f.Use(route);
        try
        {
            lock (f.Conn.Monsters)
                if (retired) f.Conn.LeavingArea = true;
                else f.Conn.ActionCount++;
        }
        finally { f.Gate.Release(); }
        await pending;
        byte[] bytes = await f.Drain();
        Assert.Equal(0, Count(bytes, [0x01, 0xab, 0, 0, 0x10, 0]));
        AssertNoAdds(bytes);
        Assert.Empty(f.Conn.Auras);
        Assert.Equal((ushort)0, f.Conn.SingleUseCooldowns.Values.Single().NativeSkillId);
    }

    [Fact]
    public async Task Remembered_duplicate_aura_skips_only_the_gem_with_unmet_instance_requirements()
    {
        using var f = new Fixture("Clarity", "Clarity");
        var gemType = Program.Items.Gem(Program.Items.Find("Metadata/Items/Gems/SkillGemClarity")!)!;
        f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
        {
            Sockets = [item.Sockets[0] with
                { Gem = item.Sockets[0].Gem! with { GemExperience = gemType.ExperienceFor(20) } }, item.Sockets[1]]
        });
        f.Conn.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] = 100;
        int effect = f.Effect("Clarity");
        Assert.NotNull(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill()));
        Assert.Null(Call("GemSkillInstanceDisabled", f.Conn, effect, f.Skill(1)));
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        await f.Set(21148, 1);
        Assert.Empty(f.Conn.Auras);
        await f.Set(21148, 0);
        var restored = Assert.Single(f.Conn.Auras).Value;
        Assert.Equal(f.Skill(1), restored.SkillId);
        Assert.Equal(1, restored.Level);
    }

    [Fact]
    public async Task Actual_stat_command_suspends_pairs_bonuses_and_reservation_then_restores_once_without_refill()
    {
        using var f = new Fixture("Clarity", "Haste");
        var baseline = Program.CharacterPlayerStats(f.Conn);
        await f.Use("instant-object");
        await f.Use("instant-object", 1);
        await Until(() => f.Conn.Auras.Count == 2);
        var active = f.Conn.Auras.Values.ToArray();
        Assert.True(f.Conn.Pools.MovementVelocityPct > 0);
        uint regen = Program.Stats.Key("base_mana_regeneration_rate_per_minute")!.Value;
        Assert.True(Program.CharacterPlayerStats(f.Conn)[regen] > baseline.GetValueOrDefault(regen));
        Assert.Equal(2, f.Conn.PresentedAuras.Count);
        await f.Drain();

        // The restriction owns only conn.Auras. RF, a flask, Fortification and a foreign received packet coexist.
        var rf = new RighteousFireState(900, 901, null, 0x104, 100, 100, 10);
        f.Conn.RighteousFire = rf;
        var utility = new UtilityFlasks.Utility(0, 121, "Fixture", [], 100, 1, 10);
        var flask = new UtilityFlasks.Active(100, 0, "Fixture", utility, 902, null,
            [new("flask", "damage_+%", 10)], 0, 100);
        f.Conn.UtilityFlaskEffects.Start(flask);
        Assert.True(Program.FortificationOf(f.Conn).State.GrantExplicitStacks(
            f.Conn.Clock.Elapsed.TotalSeconds, 7, 10000));
        await Program.Send("own-aura-test", f.Stream, f.Ciphers,
            InstanceEntities.AddBuff(0x1000, 903, active[0].BuffRow, 42, 0x105), null);
        await f.Drain();
        f.Conn.PlayerMana = 13;
        await f.Set(21148, -1);
        byte[] suspended = await f.Drain();
        Assert.Empty(f.Conn.Auras);
        Assert.Empty(f.Conn.PresentedAuras);
        Assert.Equal(13u, f.Conn.PlayerMana);
        Assert.Equal(0, f.Conn.Pools.MovementVelocityPct);
        Assert.Equal(baseline.GetValueOrDefault(regen),
            Program.CharacterPlayerStats(f.Conn).GetValueOrDefault(regen));
        Assert.Equal(new[] { "Clarity", "Haste" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
        Assert.Same(rf, f.Conn.RighteousFire);
        Assert.Same(flask, f.Conn.UtilityFlaskEffects.Of(100));
        Assert.Equal(7, Program.FortificationOf(f.Conn).Read(f.Conn.Clock.Elapsed.TotalSeconds).Current);
        Assert.Equal(0, Count(suspended, InstanceEntities.RemoveBuff(0x1000, 903)));
        foreach (var aura in active)
            foreach (var packet in AuraCatalog.EndPackets(aura, 0x1000)) Assert.Equal(1, Count(suspended, packet));
        AssertNoAdds(suspended);
        Assert.Contains(ManaPackets(suspended), p => p.Current == 13 && p.Reserved == 0);
        Assert.Contains(Skills(f.Conn), s => s.Id == f.Skill());
        ushort next = f.Conn.NextBuff;
        await f.Refresh();
        Assert.Equal(next, f.Conn.NextBuff);
        byte[] repeated = await f.Drain();
        foreach (var aura in active)
            foreach (var packet in AuraCatalog.EndPackets(aura, 0x1000)) Assert.Equal(0, Count(repeated, packet));

        // Clearing a temporary source restores enabled intent, with a clamp even at zero regeneration.
        f.Conn.PlayerStats[Program.Stats.Key("mana_regeneration_rate_+%")!.Value] = -100;
        f.Conn.PlayerMana = f.Conn.Pools.Mana;
        await f.Set(21148, 0);
        var restored = f.Conn.Auras.Values.ToArray();
        Assert.Equal(2, restored.Length);
        Assert.All(restored, a => Assert.DoesNotContain(active, old => old.Buff == a.Buff));
        uint cap = (uint)AuraCatalog.UnreservedMana(f.Conn.Pools, restored);
        Assert.Equal(cap, f.Conn.PlayerMana);
        Assert.Equal(0d, Call("ManaRegenPerSecond", f.Conn));
        Assert.True(cap < f.Conn.Pools.Mana);
        byte[] resumed = await f.Drain();
        foreach (var aura in restored)
            foreach (var packet in AuraCatalog.StartPackets(aura, 0x1000, Program.Skills.ActionHash(aura.Effect)))
                Assert.Equal(1, Count(resumed, packet));
        await f.Refresh();
        byte[] again = await f.Drain();
        AssertNoAdds(again);
        Assert.Equal(restored, f.Conn.Auras.Values);
        Assert.Equal(cap, f.Conn.PlayerMana);
    }

    [Theory]
    [InlineData("SendSkills")]
    [InlineData("SendEquipment")]
    [InlineData("SendCharacterStats")]
    [InlineData("SendPlayerMana")]
    public async Task Every_shared_source_refresh_reconciles_before_its_first_snapshot(string refresh)
    {
        using var f = new Fixture("Haste");
        await f.Use("instant-object");
        var aura = Assert.Single(f.Conn.Auras).Value;
        await f.Drain();
        f.Conn.MainAscendancy = new(Nodes: [49937]);
        await f.Refresh(refresh);
        Assert.Empty(f.Conn.Auras);
        var bytes = await f.Drain();
        var ends = AuraCatalog.EndPackets(aura, 0x1000).SelectMany(p => p).ToArray();
        int firstSnapshot = bytes.AsSpan().IndexOf(new byte[] { 0x01, 0xb3, 0, 0, 0x10, 0 });
        int remove = bytes.AsSpan().IndexOf(ends);
        Assert.True(remove >= 0 && (firstSnapshot < 0 || remove < firstSnapshot));
        AssertNoAdds(bytes);
        Assert.Equal(1, Program.CharacterPlayerStats(f.Conn)[11075]);
        f.Conn.MainAscendancy = new();
        await f.Refresh(refresh);
        Assert.Single(f.Conn.Auras);
        Assert.NotEqual(aura.Buff, f.Conn.Auras.Values.Single().Buff);
    }

    [Fact]
    public async Task Flask_pool_refresh_and_raw_reads_remove_bonuses_without_snapshot_recursion()
    {
        using var f = new Fixture("Determination", "Anger", "Vitality");
        // Restoration can exceed an interim reservation cap; no new manual affordability check.
        foreach (string id in new[] { "Determination", "Anger", "Vitality" })
            CharacterSaves.SetAuraActive(f.Conn.Character!, id, true);
        await f.Refresh();
        var before = Program.CharacterPlayerStats(f.Conn);
        Assert.True(f.Conn.Pools.Armour > 0);
        Assert.Equal(0u, f.Conn.PlayerMana);
        Assert.True(before.GetValueOrDefault(Program.Stats.Key("attack_minimum_added_fire_damage")!.Value) > 0);
        Assert.True(before.GetValueOrDefault(Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value) > 0);
        var weapon = new SkillCatalog.Unarmed(833, 2, 5, 8);
        Assert.True(Program.AttackHit(weapon, before, melee: true)[GameServer.Core.World.DamageType.Fire].Min > 0);
        f.Conn.PlayerStats[11881] = -1;
        Call("RecomputePoolsAndSpeed", f.Conn); // Same pool boundary used by flask/equipment changes.
        Assert.Empty(f.Conn.Auras);
        Assert.Equal(0, f.Conn.Pools.Armour);
        var after = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(0, after.GetValueOrDefault(Program.Stats.Key("attack_minimum_added_fire_damage")!.Value));
        Assert.Equal(0, after.GetValueOrDefault(Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value));
        Assert.Equal(0, Program.AttackHit(weapon, after, melee: true)[GameServer.Core.World.DamageType.Fire].Min);
        Assert.Equal(0u, f.Conn.PlayerMana);
        await f.Refresh();
        Assert.Empty(f.Conn.Auras);
        f.Conn.PlayerStats[11075] = 0; // Deliberate final override wins over the still-present 11881 source.
        await f.Refresh();
        Assert.Equal(3, f.Conn.Auras.Count);
        Assert.Equal(0u, f.Conn.PlayerMana);
    }

    [Fact]
    public async Task Death_while_suppressed_clears_only_this_characters_saved_intent()
    {
        using var f = new Fixture("Clarity");
        await f.Use("instant-object");
        await f.Set(21148, 1);
        await f.Drain();
        string other = "OwnAuraOther" + Guid.NewGuid().ToString("N");
        CharacterSaves.SetAuraActive(other, "Haste", true);
        f.Conn.PlayerLife = 0;
        await f.Refresh("EndAurasOnDeath");
        Assert.Empty(CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras!);
        Assert.Equal(new[] { "Haste" }, CharacterSaves.Load(other)!.ActiveAuras);
        f.Conn.PlayerLife = f.Conn.Pools.Life;
        f.Conn.AuraDeathHandled = false;
        await f.Set(21148, 0);
        Assert.Empty(f.Conn.Auras);
        AssertNoAdds(await f.Drain());
    }

    [Fact]
    public async Task Missing_gem_and_town_keep_intent_and_relog_rebuilds_fresh_runtime_state()
    {
        using var f = new Fixture("Clarity");
        await f.Use("instant-object");
        var old = Assert.Single(f.Conn.Auras).Value;
        f.Items.Remove(PlayerInventories.Weapon1, f.Host.Id);
        await f.Refresh("SendEquipment");
        Assert.Empty(f.Conn.Auras);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
        f.Items.Put(PlayerInventories.Weapon1, f.Host);
        await f.Set(21148, 1);
        Assert.Empty(f.Conn.Auras);
        await f.Set(21148, 0);
        Assert.NotEqual(old.Buff, Assert.Single(f.Conn.Auras).Value.Buff);

        ClientState Fresh(AreaLayout layout) => new(layout, 0, 1)
            { Character = f.Conn.Character, ClassRow = 1, NextBuff = 2000 };
        var town = Fresh(f.Conn.Layout with { Hash = AreaLayouts.LioneyesWatch });
        Assert.Equal(100, Call("LoadProgress", town, 100));
        Assert.Empty(town.Auras);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
        var disabled = Fresh(f.Conn.Layout);
        disabled.PlayerStats[21148] = -1; // A controlled source present before bootstrap, not a saved dev command.
        Assert.Equal(100, Call("LoadProgress", disabled, 100));
        Assert.Empty(disabled.Auras);
        Assert.Equal((ushort)2000, disabled.NextBuff);
        var reloaded = Fresh(f.Conn.Layout); // /set_stat is transient under the existing relog contract.
        Assert.Equal(100, Call("LoadProgress", reloaded, 100));
        var restored = Assert.Single(reloaded.Auras).Value;
        Assert.Empty(reloaded.PlayerStats);
        Assert.Equal((ushort)2000, restored.Buff);
        Assert.NotEqual(old.Buff, restored.Buff);
        Assert.True(AuraCatalog.ReservedMana(reloaded.Pools, reloaded.Auras.Values) > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_activation_add_rechecks_sources_and_skill_ids_before_installing_a_pair(bool removeGem)
    {
        using var f = new Fixture("Haste");
        await f.Gate.WaitAsync();
        var pending = (Task)Call("ToggleAura", "own-aura-test", f.Stream, f.Ciphers, f.Conn,
            f.Skill(), f.Effect("Haste"), "Haste", (0, 5000), 1, 0)!;
        var aura = Assert.Single(f.Conn.Auras).Value;
        uint mana = f.Conn.PlayerMana;
        try
        {
            if (removeGem) f.Items.Remove(PlayerInventories.Weapon1, f.Host.Id);
            else f.Conn.PlayerStats[21148] = 1;
        }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Conn.Auras);
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(new[] { "Haste" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
        AssertNoAdds(await f.Drain());
        if (removeGem) f.Items.Put(PlayerInventories.Weapon1, f.Host);
        await f.Set(21148, 0);
        Assert.NotEqual(aura.Buff, Assert.Single(f.Conn.Auras).Value.Buff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_or_retired_connections_cannot_restore_or_publish_queued_aura_packets(bool closed)
    {
        using var f = new Fixture("Clarity");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        await f.Gate.WaitAsync();
        Task pending = f.Refresh();
        try
        {
            f.Conn.Closed = closed;
            f.Conn.LeavingArea = !closed;
        }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Conn.Auras);
        AssertNoAdds(await f.Drain());
        Assert.Equal((ushort)1000, f.Conn.NextBuff);
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
    }

    [Fact]
    public async Task Initial_creation_precedes_restored_aura_publication_and_replay_is_once_per_object()
    {
        using var f = new Fixture("Clarity");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        f.Conn.PlayerSent = f.Conn.PlayerObjectCreated = false;
        Call("LoadProgress", f.Conn, 100);
        Assert.True(f.Conn.AuraStatsDirty);
        Assert.Empty(f.Conn.PresentedAuras);
        // The common wrapper also accepts a creation factory: it must never prefix it with aura packets.
        byte[] creation = InstanceEntities.CreateObject(0x1000, 1, [0]);
        byte[] first = (byte[])Call("WithOwnAuraChanges", f.Conn, (Func<byte[]>)(() => creation), false)!;
        Assert.Equal(creation, first);
        Assert.True(f.Conn.AuraStatsDirty);
        Assert.Empty(f.Conn.PresentedAuras);
        await f.Refresh("SendPlayer");
        byte[] bytes = await f.Drain();
        int offset = InstanceMicrotransactions.HideoutUnlocks(
            HideoutCatalog.OwnedRows.Where(row => row < 128).Select(row => (int)row)).Length;
        var reader = new LoginReader(bytes.AsSpan(offset));
        Assert.Equal(InstanceEntities.LocalPlayerId, reader.U16());
        Assert.Equal(0x1000u, reader.U32());
        Assert.Equal(InstanceEntities.CreateObjectId, reader.U16());
        Assert.Equal(0x1000u, reader.U32());
        Assert.Equal(0u, reader.U32());
        Assert.Equal((ushort)0, reader.U16());
        Assert.Equal(f.Conn.PeerType, reader.U32());
        byte[] blob = reader.Blob();
        Assert.NotEmpty(blob);
        Assert.False(reader.Short);
        int endOfCreation = offset + 6 + 18 + blob.Length;
        var aura = Assert.Single(f.Conn.Auras).Value;
        foreach (var packet in AuraCatalog.StartPackets(aura, 0x1000, Program.Skills.ActionHash(aura.Effect)))
        {
            Assert.Equal(1, Count(bytes, packet));
            Assert.True(bytes.AsSpan().IndexOf(packet) >= endOfCreation);
        }
        Assert.True(f.Conn.PlayerObjectCreated);
        await f.Refresh("SendOwnAuraChanges");
        AssertNoAdds(await f.Drain());
        // A newly created area object needs exactly one replay, despite retaining the same server aura instances.
        await f.Refresh("SendPlayer");
        bytes = await f.Drain();
        foreach (var packet in AuraCatalog.StartPackets(aura, 0x1000, Program.Skills.ActionHash(aura.Effect)))
            Assert.Equal(1, Count(bytes, packet));
    }

    [Fact]
    public async Task Source_change_while_initial_object_is_queued_rebuilds_its_stats_mana_and_replay()
    {
        using var f = new Fixture("Haste");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Haste", true);
        Call("LoadProgress", f.Conn, 100);
        f.Conn.PlayerSent = f.Conn.PlayerObjectCreated = false;
        await f.Gate.WaitAsync();
        Task pending = f.Refresh("SendPlayer");
        try { f.Conn.PlayerStats[21148] = -1; }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = f.Conn.PeerTemplate!;
        Assert.Empty(f.Conn.Auras);
        Assert.Equal(0u, initial.ManaReserved);
        Assert.Equal(0, f.Conn.Pools.MovementVelocityPct);
        Assert.Contains((11075u, 1), initial.Stats!);
        Assert.Contains(initial.Skills!, skill => skill.Stats?.Contains((15798u, 1)) == true);
        AssertNoAdds(await f.Drain());
    }

    [Theory]
    [InlineData("SendCharacterStats")]
    [InlineData("SendSkills")]
    [InlineData("SendPlayerMana")]
    public async Task Queued_player_Actor_and_mana_snapshots_sample_after_reconciling_the_new_source(string method)
    {
        using var f = new Fixture("Haste");
        await f.Use("instant-object");
        var aura = Assert.Single(f.Conn.Auras).Value;
        await f.Drain();
        f.Conn.PlayerMana = 13;
        await f.Gate.WaitAsync();
        Task pending = f.Refresh(method);
        try { f.Conn.PlayerStats[21148] = 1; }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] bytes = await f.Drain();
        Assert.Empty(f.Conn.Auras);
        foreach (var packet in AuraCatalog.EndPackets(aura, 0x1000)) Assert.Equal(1, Count(bytes, packet));
        AssertNoAdds(bytes);
        Assert.Equal(13u, f.Conn.PlayerMana);
        Assert.Equal(0, f.Conn.Pools.MovementVelocityPct);
        Assert.All(ManaPackets(bytes), p => Assert.Equal((13u, 0u), p));
        Assert.Contains((11075u, 1), Skills(f.Conn).Single(s => s.Id == f.Skill()).Stats!);
    }

    [Fact]
    public async Task Parsed_instant_aura_cannot_publish_a_late_confirmation_after_queued_source_restriction()
    {
        using var f = new Fixture("Haste");
        await f.Gate.WaitAsync();
        Task pending = f.Use("instant-object");
        Assert.Single(f.Conn.Auras); // Original accepted start is awaiting its first publication.
        try { f.Conn.PlayerStats[21148] = 1; }
        finally { f.Gate.Release(); }
        await pending;
        byte[] bytes = await f.Drain();
        AssertNoAdds(bytes);
        Assert.Equal(0, Count(bytes, [0x01, 0xce, 0, 0, 0x10, 0]));
        Assert.Empty(f.Conn.Auras);
    }

    [Fact]
    public async Task Entry_replay_revalidates_skill_identity_after_creation_consumed_the_dirty_snapshot()
    {
        using var f = new Fixture("Clarity");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Clarity", true);
        Call("LoadProgress", f.Conn, 100);
        // The initial object contains current pools/stats but no buffs; replay is a separate gate operation.
        f.Conn.AuraStatsDirty = false;
        Assert.Empty(f.Conn.PresentedAuras);
        await f.Gate.WaitAsync();
        var pending = (Task)Call("SendOwnAuraChanges", "own-aura-test", f.Stream, f.Ciphers, f.Conn, true)!;
        try { f.Items.Remove(PlayerInventories.Weapon1, f.Host.Id); }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Conn.Auras);
        AssertNoAdds(await f.Drain());
        Assert.Equal(new[] { "Clarity" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
    }

    static IEnumerable<(uint Current, uint Reserved)> ManaPackets(byte[] bytes)
    {
        byte[] header = InstanceProtocol.LifeUpdate(0x1000, 0, 0, pool: Program.ManaPool)[..12];
        for (int i = 0; i <= bytes.Length - 24; i++)
            if (bytes.AsSpan(i).StartsWith(header) && bytes[i + 20] == Program.ManaPool)
            {
                yield return (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 16)));
                i += 23;
            }
    }
}
