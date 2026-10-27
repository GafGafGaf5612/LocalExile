using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class UpstreamProjectileProfileTests
{
    public UpstreamProjectileProfileTests() => TestSaves.Ensure();
    static Func<string, int> Stats(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
    static ProjectileProfile Profile(string effect, Func<string, int>? stats = null) =>
        GatewayUpstreamProjectileProfiles.Resolve(effect,
            effect == "Fireball" ? "Metadata/Projectiles/Fireball" : "Metadata/Projectiles/WandProjectile",
            stats ?? (_ => 0), 100, effect != "Fireball");

    static void ReleaseReturning(ProjectileRuntimeProbe world, ProjectileProfile profile)
    {
        var owner = new ProjectileActorKey(1000);
        world.Targets.Add(new(owner, 1, _ => new(10, 10), 0));
        world.Runtime.Release(new(profile, owner, owner, 1, new(10, 10), new(110, 10)) { ReturnAnchor = owner });
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-150)]
    public void Return_speed_reductions_keep_upstream_one_cell_per_second_floor(int reduction)
    {
        var p = GatewayUpstreamProjectileProfiles.Resolve("Fireball", "Metadata/Projectiles/Fireball",
            Stats(("returned_projectile_speed_+%", reduction)), 37, false);
        Assert.Equal(1, p.Speed * p.ReturnSpeedMultiplier, 10);
    }

    [Fact]
    public void Slow_return_survives_ten_seconds_and_finishes_at_upstream_overshoot()
    {
        var world = new ProjectileRuntimeProbe();
        var p = GatewayUpstreamProjectileProfiles.Resolve("Fireball", "Metadata/Projectiles/Fireball",
            Stats(("projectiles_return", 1), ("returned_projectile_speed_+%", -100)), 20, false) with { Range = 20 };
        ReleaseReturning(world, p); world.Advance(11);
        var flying = Assert.Single(world.Runtime.Projectiles);
        Assert.Equal(1, flying.Phase); Assert.InRange(flying.Position.X, 20, 20.001);
        world.Advance(171.1);
        Assert.Empty(world.Runtime.Projectiles);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Returned, end.Reason); Assert.Equal(-140, end.Position.X, 8);
    }

    [Fact]
    public void Fireball_same_line_siblings_each_hit_and_explode_on_the_near_victim()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 80, 10);
        world.Release(Profile("Fireball", Stats(("number_of_additional_projectiles", 1))) with
            { SpreadRadians = 0, Radius = 1 }); world.Advance(1);
        Assert.Equal(new uint[] { 1, 1 }, world.Attempts.Where(h => h.Stage == "direct").Select(h => h.Target.Id));
        Assert.DoesNotContain(world.Attempts, h => h.Target.Id == 2);
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        var ends = world.Events.Where(e => e.Kind == ProjectileEventKind.End).Select(e => e.Position.X).Order().ToArray();
        Assert.Equal(2, ends.Length); Assert.All(ends, x => Assert.InRange(x, 39, 39.001));
    }

    [Theory]
    [InlineData("Spark")]
    [InlineData("BurningArrow")]
    public void Unnamed_catalog_projectiles_admit_each_outbound_sibling_independently(string effect)
    {
        Assert.Null(Program.ProjectileDefinition(effect));
        var definition = Program.CatalogProjectileDefinition(Program.Skills.EffectRow(effect));
        Assert.NotNull(definition);
        var profile = definition.Resolve(Stats(("number_of_additional_projectiles", 1)), 100)
            with { SpreadRadians = 0, Radius = 1 };
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 80, 10);
        world.Release(profile); world.Advance(1);
        Assert.Equal(new uint[] { 1, 1 }, world.Attempts.Where(h => h.Stage == "direct").Select(h => h.Target.Id));
        Assert.DoesNotContain(world.Attempts, h => h.Target.Id == 2);
    }

    [Fact]
    public void Fireball_outbound_siblings_hit_independently_but_share_one_returned_salvo_ledger()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 0, 10);
        ReleaseReturning(world, Profile("Fireball", Stats(("projectiles_return", 1), ("number_of_additional_projectiles", 1)))
            with { SpreadRadians = 0, Radius = 1 });
        world.Advance(4);
        Assert.Equal(2, world.Attempts.Count(h => h.Phase == 0 && h.Stage == "direct" && h.Target.Id == 1));
        var returned = Assert.Single(world.Attempts, h => h.Phase == 1);
        Assert.Equal("direct", returned.Stage); Assert.Equal(2u, returned.Target.Id);
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.End && e.Reason == ProjectileEndReason.Returned));
    }

    [Theory]
    [InlineData("KineticBlast")]
    [InlineData("KineticBlastAltX")]
    [InlineData("KineticBlastAltY")]
    public void KB_outbound_hits_are_independent_but_returned_salvo_shares_direct_and_centered_area(string effect)
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 0, 10); world.Target(3, 0, 18);
        ReleaseReturning(world, Profile(effect, Stats(("projectiles_return", 1), ("number_of_additional_projectiles", 1)))
            with { SpreadRadians = 0, Radius = 1 });
        world.Advance(4);
        Assert.Equal(2, world.Attempts.Count(h => h.Stage == "direct" && h.Phase == 0 && h.Target.Id == 1));
        Assert.Equal(2, world.Attempts.Count(h => h.Phase == 1));
        Assert.Equal("direct", Assert.Single(world.Attempts, h => h.Phase == 1 && h.Target.Id == 2).Stage);
        var area = Assert.Single(world.Attempts, h => h.Phase == 1 && h.Target.Id == 3);
        Assert.Equal("upstream-kinetic-blast", area.Stage); Assert.Equal(1, area.Multiplier);
        Assert.InRange(area.Position.X, 0, 1.01); Assert.Equal(10, area.Position.Y);
        Assert.Equal(3, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.End && e.Reason == ProjectileEndReason.Returned));
    }

    [Fact]
    public void Fireball_outbound_area_is_immediate_and_return_contacts_are_direct_only()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 80, 10); world.Target(2, 80, 18);
        world.Target(3, 0, 10); world.Target(4, 0, 18);
        ReleaseReturning(world, Profile("Fireball", Stats(("projectiles_return", 1))) with { Radius = 1 });
        world.Advance(.7);
        Assert.Single(world.Attempts, h => h.Stage == "direct" && h.Phase == 0 && h.Target.Id == 1);
        Assert.Single(world.Attempts, h => h.Stage == "upstream-fireball" && h.Phase == 0 && h.Target.Id == 2);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.Equal(1, Assert.Single(world.Runtime.Projectiles).Phase);
        world.Advance(4);
        Assert.Single(world.Attempts, h => h.Phase == 1 && h.Stage == "direct" && h.Target.Id == 3);
        Assert.DoesNotContain(world.Attempts, h => h.Target.Id == 4);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    public void Exhausted_return_budget_flies_through_remaining_targets_and_terrain_to_overshoot(string effect)
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 0, 10); world.Target(2, -40, 10);
        world.Blocked = (x, _) => x == -20;
        ReleaseReturning(world, Profile(effect, Stats(("projectiles_return", 1))) with { Radius = 1 });
        world.Advance(5);
        Assert.Single(world.Attempts, h => h.Phase == 1 && h.Stage == "direct" && h.Target.Id == 1);
        Assert.DoesNotContain(world.Attempts, h => h.Target.Id == 2);
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(ProjectileEndReason.Returned, end.Reason); Assert.Equal(-140, end.Position.X, 8);
    }

    [Theory]
    [InlineData("Fireball", false)]
    [InlineData("Fireball", true)]
    [InlineData("KineticBlast", false)]
    [InlineData("KineticBlast", true)]
    public void Piercing_attempt_budget_preserves_flight_and_suppresses_terminal_area_after_contact(string effect, bool wall)
    {
        var world = new ProjectileRuntimeProbe();
        world.Target(1, 40, 10); world.Target(2, 65, 10); world.Target(3, 95, 10);
        if (wall) world.Blocked = (x, _) => x == 105;
        world.Release(Profile(effect, Stats(("projectile_number_of_targets_to_pierce", 1))) with { Radius = 1 });
        world.Advance(2);
        Assert.Equal(new uint[] { 1, 2 }, world.Attempts.Where(h => h.Stage == "direct").Select(h => h.Target.Id));
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        var end = Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End);
        Assert.Equal(wall ? ProjectileEndReason.Terrain : ProjectileEndReason.Distance, end.Reason);
        Assert.Equal(wall ? 104.5 : 130, end.Position.X, 8);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    [InlineData("KineticBlastAltX")]
    [InlineData("KineticBlastAltY")]
    public void No_hit_piercing_control_still_explodes_on_terrain(string effect)
    {
        var world = new ProjectileRuntimeProbe { Blocked = (x, _) => x == 60 };
        world.Target(1, 60, 18);
        world.Release(Profile(effect, Stats(("always_pierce", 1))) with { Radius = 1 }); world.Advance(1);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
        Assert.Single(world.Attempts, h => h.Stage.StartsWith("upstream-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Spark")]
    [InlineData("SparkAltX")]
    [InlineData("SparkAltY")]
    [InlineData("RainOfArrows")]
    [InlineData("BlastRain")]
    [InlineData("SplittingSteel")]
    [InlineData("VenomGyre")]
    [InlineData("WinterOrb")]
    [InlineData("ForbiddenRite")]
    [InlineData("Cremation")]
    [InlineData("RollingMagma")]
    [InlineData("ToxicRain")]
    [InlineData("PoisonousConcoction")]
    public void Named_return_restrictions_keep_basic_flight_outbound(string effect)
    {
        var world = new ProjectileRuntimeProbe();
        ReleaseReturning(world, Profile(effect, Stats(("projectiles_return", 1))) with { Range = 20 });
        world.Advance(1);
        Assert.DoesNotContain(world.Events, e => e.Kind == ProjectileEventKind.Redirect);
        Assert.Equal(ProjectileEndReason.Distance, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Theory]
    [InlineData("KineticBlast")]
    [InlineData("KineticBlastAltX")]
    [InlineData("KineticBlastAltY")]
    public void All_upstream_KB_variants_keep_primary_projectiles_and_one_centered_full_damage_area(string effect)
    {
        var p = Profile(effect, Stats(("number_of_additional_projectiles", 4), ("cluster_burst_spawn_amount", 9)));
        Assert.Equal(5, p.Count);
        Assert.True(p.DirectDamage);
        Assert.Equal(ProjectileCountConsumer.Primary, p.CountConsumer);
        Assert.Equal(1, p.ContactArea!.Count);
        Assert.Equal(0, p.ContactArea.Offset);
        Assert.Equal(1, p.ContactArea.Multiplier);
        Assert.True(p.ContactArea.ExcludeContactTarget);
        Assert.Equal(ProjectileHitScope.Projectile, p.HitRule.Scope);
    }

    [Fact]
    public void Fireball_pierce_contacts_explode_once_without_empty_range_or_return_area()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 80, 10);
        var p = Profile("Fireball", Stats(("projectile_number_of_targets_to_pierce", 1))) with { Speed = 10, Radius = 1, Range = 100 };
        world.Release(p); world.Advance(10.01);
        Assert.Equal(2, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Equal(2, world.Attempts.Count(h => h.Stage == "direct"));
        Assert.DoesNotContain(world.Attempts, h => h.Stage == "upstream-fireball");
        Assert.False(p.ContactAreaOnReturn);
        Assert.Null(p.EndArea);
    }

    [Fact]
    public void Upstream_KB_area_excludes_its_contact_victim_but_hits_an_off_path_neighbour()
    {
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10); world.Target(2, 40, 18);
        world.Release(Profile("KineticBlast") with { Speed = 10, Radius = 1 }); world.Advance(3.1);
        Assert.Equal("direct", Assert.Single(world.Attempts, h => h.Target.Id == 1).Stage);
        Assert.Equal("upstream-kinetic-blast", Assert.Single(world.Attempts, h => h.Target.Id == 2).Stage);
        Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.Area);
    }

    [Theory]
    [InlineData("Fireball", 0)]
    [InlineData("KineticBlast", 1)]
    public void Empty_flight_explodes_only_for_upstreams_KB_policy(string effect, int areas)
    {
        var world = new ProjectileRuntimeProbe();
        world.Release(Profile(effect) with { Speed = 100, Range = 100 }); world.Advance(1.01);
        Assert.Equal(areas, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
    }

    [Fact]
    public void Catalog_policy_keeps_upstream_count_cap_fixed_gap_additive_pierce_and_return_controls()
    {
        var p = Profile("KineticBlast", Stats(("number_of_additional_projectiles", 100),
            ("projectile_number_of_targets_to_pierce", 2), ("projectile_base_number_of_targets_to_pierce", 4),
            ("arrow_number_of_targets_to_pierce", 1), ("arrow_base_number_of_targets_to_pierce", 3)));
        Assert.Equal(20, p.Count); Assert.Equal(-1, p.Pierce); Assert.Equal(11, p.OutboundHitLimit); Assert.Equal(11, p.ReturnHitLimit);
        Assert.Equal(0, p.AimSpreadDistance); Assert.Equal(19 * Math.PI / 18, p.SpreadRadians, 10);
        Assert.Equal(150, p.ReturnOvershoot); Assert.True(p.IgnoreTerrainOnReturn); Assert.True(p.ExcludeTurnaroundTarget);
        Assert.True(Profile("Spark").CannotReturn);
        Assert.False(Profile("FreezingPulse").ReturnOnDistance);
    }

    [Fact]
    public void Minimal_provider_keeps_catalog_flight_without_installing_deferred_special_recipes()
    {
        foreach (string effect in new[] { "IceSpear", "IceSpearAltX", "BallLightning", "Spark", "EyeOfWinter",
                     "RollingMagma", "PoisonousConcoction", "PoisonousConcoctionAltX", "TornadoShot" })
            Assert.Null(Program.ProjectileDefinition(effect));
        var spark = Program.CatalogProjectileDefinition(Program.Skills.EffectRow("Spark"))!.Resolve(_ => 0, 100);
        Assert.Null(spark.Pulse); Assert.Null(spark.TravelEmission); Assert.Null(spark.EndEmission);
        Assert.Equal(0, spark.TerrainBounces); Assert.Equal(0, spark.WanderInterval);
        Assert.True(spark.CannotReturn);
    }
}

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Returning_Fireball_delays_ordinary_contact_art_but_keeps_piercing_art_immediate(int pierce, bool immediate)
    {
        using var f = new Fixture("Fireball");
        f.Conn.PlayerStats[Program.Stats.Key("projectiles_return")!.Value] = 1;
        f.Conn.PlayerStats[Program.Stats.Key("projectile_number_of_targets_to_pierce")!.Value] = pierce;
        await f.Gate.WaitAsync();
        bool gateOwned = true;
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground").IsCompletedSuccessfully);
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                for (int i = 0; i < 120 && session.Runtime.Projectiles.Count == 0; i++)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
                var projectile = Assert.Single(session.Runtime.Projectiles);
                var presentation = Assert.IsType<ProjectileOffense>(projectile.Use.Payload).Presentation!;
                Assert.True(projectile.Profile.Returns);
                var probe = new ProjectileRuntimeProbe(); probe.Target(1, 40, 10);
                var owner = new ProjectileActorKey(1000);
                probe.Targets.Add(new(owner, 1, _ => new(10, 10), 0));
                probe.Runtime.Release(new(projectile.Profile with { Speed = 100, Radius = 1, Range = 100 },
                    owner, owner, 1, new(10, 10), new(110, 10)) { ReturnAnchor = owner });
                probe.Advance(.4);
                var contact = Assert.Single(probe.Events, e => e.Kind == ProjectileEventKind.Area);
                string art = "Metadata/Effects/Spells/fireball/explode_new2.ao";
                Assert.Equal(immediate ? art : null, presentation.EventEffect!(contact));
                probe.Advance(4);
                var end = Assert.Single(probe.Events, e => e.Kind == ProjectileEventKind.End);
                Assert.Equal(ProjectileEndReason.Returned, end.Reason);
                Assert.Equal(art, presentation.EventEffect(end));
            }
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate();
            if (gateOwned) { gateOwned = false; f.Gate.Release(); }
            if (f.Conn.ProjectileRuntime is { } session) await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Migrated_area_uses_the_existing_supported_area_stat_path_once()
    {
        using var f = new Fixture("Fireball");
        f.Conn.PlayerStats[Program.Stats.Key("base_skill_area_of_effect_+%")!.Value] = 100;
        f.Conn.PlayerStats[Program.Stats.Key("skill_area_of_effect_+%")!.Value] = 200;
        var supported = Call("SupportedSkill", f.Conn, f.Skill())!;
        var own = (Dictionary<uint, int>)supported.GetType().GetProperty("Stats")!.GetValue(supported)!;
        int expected = (int)Call("SkillRadius", f.Conn, own, true, f.Effect("Fireball"))!;
        await f.Gate.WaitAsync();
        bool gateOwned = true;
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground").IsCompletedSuccessfully);
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                for (int i = 0; i < 120 && session.Runtime.Projectiles.Count == 0; i++)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
                Assert.Equal(expected, Assert.Single(session.Runtime.Projectiles).Profile.ContactArea!.Radius);
            }
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate();
            if (gateOwned) { gateOwned = false; f.Gate.Release(); }
            if (f.Conn.ProjectileRuntime is { } session) await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("Fireball", "Metadata/Effects/Spells/fireball/explode_new2.ao")]
    [InlineData("KineticBlast", "Metadata/Effects/Spells/cluster_burst/big.ao")]
    [InlineData("KineticBlastAltX", "Metadata/Effects/Spells/cluster_burst/big.ao")]
    [InlineData("KineticBlastAltY", "Metadata/Effects/Spells/cluster_burst/big.ao")]
    public async Task Migrated_upstream_input_commits_direct_and_neighbour_damage_before_native_output(string effect, string art)
    {
        bool fireball = effect == "Fireball";
        using var f = new Fixture(fireball ? "Fireball" : "KineticBlast");
        if (!fireball)
        {
            var type = Program.Items.Find("Metadata/Items/Gems/SkillGemKineticBlast")!;
            var variant = Program.Items.Gem(type)!.Variants.First(v => v.Effect == Program.Skills.EffectRow(effect));
            f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
                { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { GemVariant = variant.Index } }] });
        }
        // Replacing a gem's effect deliberately retires its old skill id. Use the newly assigned native id.
        var active = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn.Character!)!).Single(g => g.Effect == f.Effect(effect));
        int skillIndex = Program.GemSkillIdPool.Value.ToList().IndexOf(active.Id);
        Assert.True(skillIndex >= 0);
        f.Conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        foreach (string boundary in new[] { "minimum", "maximum" })
            f.Conn.PlayerStats[Program.Stats.Key((fireball ? "spell_" : "attack_") + boundary + "_added_chaos_damage")!.Value] = 100;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        var direct = new SpawnedMonster("direct migration target", new(45, 32, 0), 0, 2, 10000);
        var nearby = new SpawnedMonster("off-path migration target", new(45, 40, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, direct); f.Conn.Monsters.Add(43, nearby);
        double maximum = Enum.GetValues<DamageType>().Sum(t => ((DamagePacket)Call("SupportedHit", f.Conn, active.Id)!)[t].Max);
        await f.Gate.WaitAsync();
        bool gateOwned = true;
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground", skillIndex).IsCompletedSuccessfully);
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                for (int i = 0; i < 120 && direct.Life == 10000; i++)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
                Assert.True(direct.Life < 10000);
                Assert.True(nearby.Life < 10000, "The existing upstream contact area must hit an off-path neighbour.");
                Assert.InRange((double)(10000 - direct.Life), 1d, Math.Ceiling(maximum));
            }
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Conn.ProjectileRuntime!.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
            gateOwned = false; f.Gate.Release();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await f.Drain()).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(art)) >= 0);
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate();
            if (gateOwned) { gateOwned = false; f.Gate.Release(); }
            if (f.Conn.ProjectileRuntime is { } session) await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
