using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Inherited Fortune and every small node grant ordinary passive points; the notables carry the class stats.
    static readonly ushort[] AristocratGems = [54114, 48846, 24607, 46836, 61147, 35022, 27473, 30851];
    static readonly ushort[] AristocratAttributes = [54114, 48846, 64905, 11154, 55065, 2748, 35432, 2688];
    static readonly ushort[] AristocratTuition = [54114, 48846, 24607, 46836, 34482, 50743, 21905, 2688];

    static int AristocratStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static void ReplaceHelmet(GatewayCharacter game, Item gem, Item? support = null)
    {
        if (game.Items.Items(PlayerInventories.Helmet).SingleOrDefault() is { } host)
            Assert.NotNull(game.Items.Remove(PlayerInventories.Helmet, host.Id));
        EquipFamilyGem(game, gem, support);
    }

    // A skill record also carries character stats: the nodes' own stats and the gear's armour (pools refresh on
    // allocation) differ from the class-less baseline without being skill values.
    static readonly string[] CharacterOnly = ["all_skill_gem_level_+", "non_exceptional_support_gem_level_+",
        "all_skill_gem_quality_+", "global_item_attribute_requirements_+%", "physical_damage_reduction_rating"];

    static (uint Row, string Stats)[] SkillRecords(ClientState state) => [.. FamilySkills(state)
        .Select(s => (s.EffectsPerLevelRow, string.Join(",", (s.Stats ?? [])
            .Where(v => !CharacterOnly.Any(n => Program.Stats.Key(n) == v.Key)).Select(v => $"{v.Key}={v.Value}"))))];

    static uint[] SupportRecords(ClientState state) =>
        [.. FamilySkills(state).SelectMany(s => s.Supports).Select(s => s.EffectsPerLevelRow)];

    [Fact]
    public void Aristocrat_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(4, AscendancyFamily.Phrecia, "Aristocrat")!;
        Assert.Equal(new ushort[] { 33795, 54114, 61147, 24607, 27473, 34482, 3952, 46836, 48846, 35022, 30851, 50743,
            11154, 35432, 2748, 64905, 55065, 2688, 21905 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            AristocratGems.Concat(AristocratAttributes).Concat(AristocratTuition).Append((ushort)3952).Distinct().Order());
        Assert.Equal(5, definition.Nodes[48846].PointsGranted);
        Assert.All(definition.Nodes.Values.Where(n => n.Name == "Passive Point"), n => Assert.Equal(1, n.PointsGranted));
    }

    [Fact]
    public async Task Aristocrat_gem_nodes_raise_active_level_and_quality_and_non_exceptional_supports_only()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Aristocrat", refundPoints: 40, level: 100, god: true);
        // Baseline without the class: what the effective gems must equal once the nodes are allocated.
        ReplaceHelmet(game, FamilyGem("Fireball", 19) with { Quality = 27 }, FamilyGem("Added Fire Damage Support", 5));
        var activeTarget = SkillRecords(game.State);
        var supportTarget = SupportRecords(game.State);
        ReplaceHelmet(game, FamilyGem("Fireball", 19), FamilyGem("Added Fire Damage Support", 5));
        Assert.NotEqual(activeTarget, SkillRecords(game.State)); // Fireball's quality stats make the 20 visible.
        ReplaceHelmet(game, FamilyGem("Fireball", 20), FamilyGem("Empower Support", 3));
        var empowerBaseline = SupportRecords(game.State);

        var gem = FamilyGem("Fireball", 18);
        ReplaceHelmet(game, gem, FamilyGem("Added Fire Damage Support", 1));
        Assert.NotEqual(supportTarget, SupportRecords(game.State));
        Assert.Null((await game.Change(AristocratGems, allocate: true)).Error);
        Assert.Equal(8, Program.AscendancyRules.SpentPoints(4, game.State.MainAscendancy));
        Assert.Equal(9, Program.AscendancyRules.OrdinaryPointsGranted(4, game.State.MainAscendancy));
        Assert.Equal(1, AristocratStat(game.State, "all_skill_gem_level_+"));
        // Intrinsic 18 quality 7 + Family Heirlooms 1 + Centerpiece 20 == a level 19 quality 27 gem.
        ReplaceHelmet(game, gem with { Quality = 7 }, FamilyGem("Added Fire Damage Support", 1));
        Assert.Equal(activeTarget, SkillRecords(game.State));
        Assert.Equal(supportTarget, SupportRecords(game.State)); // Gem-lined Cap: level 1 + 4 == level 5.
        Assert.Equal(activeTarget, SkillRecords(game.Reload()));
        Assert.Equal(18, Program.Items.GemLevel(gem)); // Stored gems never change.
        ReplaceHelmet(game, FamilyGem("Fireball", 20), FamilyGem("Empower Support", 3));
        Assert.Equal(empowerBaseline, SupportRecords(game.State)); // Empower is Exceptional.

        ReplaceHelmet(game, gem with { Quality = 7 }, FamilyGem("Added Fire Damage Support", 1));
        Assert.Null((await game.Change([35022], allocate: false, refundPoints: true)).Error);
        Assert.Null((await game.Change([30851], allocate: false, refundPoints: true)).Error);
        Assert.NotEqual(supportTarget, SupportRecords(game.State));
        Assert.NotEqual(activeTarget, SkillRecords(game.State));
    }

    [Fact]
    public async Task Aristocrat_requirement_nodes_scale_gem_and_item_requirements_rounding_down()
    {
        if (!DataAvailable()) return;
        Assert.Equal(116, Program.ScaledRequirement(155, -25));
        Assert.Equal(193, Program.ScaledRequirement(155, 25));
        Assert.Equal(0, Program.ScaledRequirement(155, -150));
        Assert.Equal(0, Program.ScaledRequirement(0, 50));
        using var game = new GatewayCharacter(choice: "Aristocrat", refundPoints: 40, level: 100);
        var gem = FamilyGem("Fireball", 18);
        int Needs() => int.Parse(System.Text.RegularExpressions.Regex.Match(
            ProgramValue<string>("GemRequirementsUnmet", game.State, gem), @"requires (\d+) intelligence").Groups[1].Value);
        int baseline = Needs();
        Assert.Null((await game.Change(AristocratTuition[..4], allocate: true)).Error);
        Assert.Equal(-25, ProgramValue<int>("AttributeRequirementsPct", game.State));
        Assert.Equal(Program.ScaledRequirement(baseline, -25), Needs());
        Assert.Null((await game.Change(AristocratTuition[4..], allocate: true)).Error);
        Assert.Equal(25, ProgramValue<int>("AttributeRequirementsPct", game.State)); // -25 + Dreadful Attitude's +50.
        Assert.Equal(Program.ScaledRequirement(baseline, 25), Needs());
        Assert.Equal(Program.ScaledRequirement(baseline, 25), Needs()); // Fresh reads never accumulate.
        Assert.Equal(25, ProgramValue<int>("AttributeRequirementsPct", game.Reload()));
    }

    [Fact]
    public async Task Aristocrat_attribute_nodes_grant_conditional_immunity_and_lowest_attribute_damage()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Aristocrat", refundPoints: 40, level: 90);
        int baseDamage = AristocratStat(game.State, "damage_+%");
        Assert.Null((await game.Change(AristocratAttributes, allocate: true)).Error);
        var state = game.State;
        int str = AristocratStat(state, "strength"), dex = AristocratStat(state, "dexterity"), intel = AristocratStat(state, "intelligence");
        // Duelist 23/23/14 with Illicit Gains and Fancy Foil: Strength equals Dexterity, both above Intelligence.
        Assert.Equal(str, dex);
        Assert.True(dex > intel);
        Assert.Equal(baseDamage + 8 * (Math.Min(str, Math.Min(dex, intel)) / 5), AristocratStat(state, "damage_+%"));

        var hit = DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamageType.Cold, new(100, 100))
            .Add(DamageType.Lightning, new(100, 100));
        var stats = Program.CharacterPlayerStats(state);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var ailments = new PlayerElementalAilments();
        ailments.Apply(hit, hit, critical: true, maxLife: 1000, now: 0, Get, new Random(1));
        var effects = ailments.Snapshot(0);
        Assert.False(effects.Freeze); // Dexterity higher than Intelligence.
        Assert.True(effects.Ignite); // Strength is not strictly higher than Dexterity.
        Assert.True(effects.Shock > 0); // Private Tuition is not allocated.

        // Refunding Fancy Foil removes the flag; Dexterity still exceeds Intelligence, so freezes land again.
        Assert.Null((await game.Change([11154], allocate: false, refundPoints: true)).Error);
        stats = Program.CharacterPlayerStats(game.State);
        Assert.True(Get("dexterity") > Get("intelligence"));
        ailments.Clear();
        ailments.Apply(hit, hit, critical: true, maxLife: 1000, now: 0, Get, new Random(1));
        Assert.True(ailments.Snapshot(0).Freeze);
        Assert.Null((await game.Change([11154], allocate: true)).Error);
        Assert.Equal(0, AristocratStat(game.Reload(), "cannot_be_frozen") + (Program.CharacterPlayerStats(game.Reload())
            .GetValueOrDefault(Program.Stats.Key("cannot_be_frozen_with_dex_higher_than_int")!.Value) == 1 ? 0 : 1));
    }

    [Fact]
    public async Task Aristocrat_conditions_track_live_attributes_and_reconcile_existing_ailments()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Aristocrat", refundPoints: 40, level: 90);
        Assert.Null((await game.Change([54114, 48846, 55065, 2748], allocate: true)).Error); // Illicit Gains only.
        var stats = Program.CharacterPlayerStats(game.State);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.True(Get("strength") > Get("dexterity"));
        var ailments = new PlayerElementalAilments();
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100);
        ailments.Apply(hit, hit, critical: true, maxLife: 1000, now: 0, Get, new Random(1));
        Assert.False(ailments.Snapshot(0).Ignite);
        ailments.Apply(hit, hit, critical: true, maxLife: 1000, now: 0, _ => 0, new Random(1));
        Assert.True(ailments.Snapshot(0).Ignite);
        ailments.Reconcile(Get); // Becoming immune removes an existing ignite.
        Assert.False(ailments.Snapshot(0).Ignite);
    }
}
