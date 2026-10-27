using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileModifierBoundaryTests
{
    static ProjectileProfile Resolve(ProjectileProfile profile, Func<string, int> get, bool attack = false,
        bool chainInputConsumed = false, bool intrinsicSplitCount = false) =>
        GatewayProjectileModifiers.Resolve(profile, get, attack, chainInputConsumed, intrinsicSplitCount);

    static Func<string, int> Stats(params (string Name, int Value)[] values)
    {
        var stats = values.ToDictionary(v => v.Name, v => v.Value);
        return name => stats.GetValueOrDefault(name);
    }

    [Fact]
    public void Generic_binding_resolves_a_synthetic_profile_without_a_skill_recipe()
    {
        var profile = Resolve(new("synthetic") { Count = 2 }, Stats(("number_of_additional_projectiles", 4)));
        Assert.Equal(6, profile.Count);
    }

    [Theory]
    [InlineData(false, 8, 4)]
    [InlineData(true, 7, 9)]
    public void Raw_and_virtual_aliases_use_the_larger_grant_and_attack_inputs_stay_scoped(
        bool attack, int count, int pierce)
    {
        var profile = Resolve(new("synthetic") { Count = 2, Pierce = 1, Fork = 1, Chain = 1, Split = 1 }, Stats(
            ("number_of_additional_projectiles", 2), ("attacks_number_of_additional_projectiles", 3),
            ("spells_number_of_additional_projectiles", 4), ("projectile_number_of_targets_to_pierce", 2),
            ("projectile_base_number_of_targets_to_pierce", 3), ("arrow_number_of_targets_to_pierce", 4),
            ("arrow_base_number_of_targets_to_pierce", 5), ("projectiles_fork", 1), ("virtual_projectiles_fork", 2),
            ("number_of_additional_forks_base", 1), ("attack_projectiles_fork", 2), ("arrows_fork", 3),
            ("attack_projectiles_fork_additional_times", 1), ("projectiles_from_spells_fork", 4),
            ("number_of_chains", 2), ("virtual_number_of_chains_for_projectiles", 3),
            ("projectile_number_to_split", 2), ("projectile_number_of_targets_to_split_to", 4),
            ("intermediary_projectile_number_of_targets_to_split_to", 3)), attack);
        Assert.Equal(count, profile.Count);
        Assert.Equal(pierce, profile.Pierce);
        Assert.Equal(8, profile.Fork);
        Assert.Equal(4, profile.Chain);
        Assert.Equal(5, profile.Split);
    }

    [Theory]
    [InlineData("always_pierce", false)]
    [InlineData("virtual_always_pierce", false)]
    [InlineData("arrows_always_pierce", true)]
    public void Unlimited_pierce_is_preserved(string stat, bool attack)
    {
        Assert.Equal(-1, Resolve(new("synthetic") { Pierce = 2 }, Stats((stat, 1)), attack).Pierce);
        Assert.Equal(-1, Resolve(new("synthetic") { Pierce = -1 }, Stats()).Pierce);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prohibitions_survive_together_with_grants_for_runtime_precedence(bool attack)
    {
        var profile = Resolve(new("synthetic") { Pierce = 2, Fork = 2, Chain = 2, Split = 2, Returns = true }, Stats(
            ("cannot_pierce", 1), ("virtual_projectiles_cannot_fork", 1), ("virtual_projectiles_cannot_chain", 1),
            ("projectiles_cannot_split", 1), ("skill_projectiles_cannot_return", 1)), attack);
        Assert.True(profile.CannotPierce);
        Assert.True(profile.CannotFork);
        Assert.True(profile.CannotChain);
        Assert.True(profile.CannotSplit);
        Assert.True(profile.CannotReturn);
        Assert.Equal(2, profile.Pierce);
        Assert.Equal(2, profile.Fork);
        Assert.Equal(2, profile.Chain);
        Assert.Equal(2, profile.Split);
        Assert.True(profile.Returns);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Spell_prohibitions_do_not_restrict_attacks(bool attack, bool prohibited)
    {
        var profile = Resolve(new("synthetic"), Stats(("projectiles_from_spells_cannot_pierce", 1),
            ("projectiles_from_spells_cannot_fork", 1), ("projectiles_from_spells_cannot_chain", 1)), attack);
        Assert.Equal(prohibited, profile.CannotPierce);
        Assert.Equal(prohibited, profile.CannotFork);
        Assert.Equal(prohibited, profile.CannotChain);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(150, true)]
    public void Return_chance_grants_only_guaranteed_returns(int chance, bool returns)
    {
        Assert.Equal(returns, Resolve(new("synthetic"), Stats(("projectile_return_%_chance", chance))).Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Attack_return_grants_stay_scoped(bool attack, bool returns)
    {
        Assert.Equal(returns, Resolve(new("synthetic"), Stats(("attack_projectiles_return", 1)), attack).Returns);
        Assert.True(Resolve(new("synthetic"), Stats(("projectiles_return", 1)), attack).Returns);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 2)]
    public void Consumed_chain_input_is_not_applied_again(bool consumed, int chains)
    {
        var profile = Resolve(new("synthetic") { Chain = 2, GroundChains = 4 },
            Stats(("number_of_chains", 3)), chainInputConsumed: consumed);
        Assert.Equal(chains, profile.Chain);
        Assert.Equal(4, profile.GroundChains);
    }

    [Fact]
    public void Generic_split_conversion_adds_extra_count_once_and_keeps_existing_split_grants()
    {
        var profile = Resolve(new("synthetic") { Count = 3, Split = 2 }, Stats(
            ("number_of_additional_projectiles", 4), ("projectile_number_to_split", 3),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1)));
        Assert.Equal(1, profile.Count);
        Assert.Equal(9, profile.Split);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Intrinsic_split_count_consumes_extra_once_even_when_generic_conversion_is_enabled(int conversion)
    {
        var profile = Resolve(new("synthetic") { Split = 3 }, Stats(("number_of_additional_projectiles", 4),
            ("projectile_number_to_split", 3),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", conversion)), intrinsicSplitCount: true);
        Assert.Equal(1, profile.Count);
        Assert.Equal(7, profile.Split);
    }

    [Theory]
    [InlineData(ProjectileCountConsumer.ContactAreas)]
    [InlineData(ProjectileCountConsumer.GroundChains)]
    [InlineData(ProjectileCountConsumer.Children)]
    public void Competing_count_conversions_are_rejected(ProjectileCountConsumer consumer)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(new("synthetic") { CountConsumer = consumer },
            Stats(("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1))));
    }

    [Fact]
    public void Children_inherit_continuation_modifiers_without_expanding_their_count_twice()
    {
        var profile = Resolve(new("synthetic")
        {
            CountConsumer = ProjectileCountConsumer.Children,
            EndEmission = new(new("end-child") { Count = 3 }),
            TravelEmission = new(new("travel-child") { Count = 1 })
        }, Stats(("number_of_additional_projectiles", 4), ("projectile_number_of_targets_to_pierce", 2),
            ("number_of_chains", 3), ("projectiles_return", 1), ("skill_projectiles_cannot_return", 1)));
        Assert.Equal(1, profile.Count);
        Assert.Equal(7, profile.EndEmission!.Child.Count);
        Assert.Equal(1, profile.TravelEmission!.Child.Count);
        foreach (var child in new[] { profile.EndEmission.Child, profile.TravelEmission.Child })
        {
            Assert.Equal(2, child.Pierce);
            Assert.Equal(3, child.Chain);
            Assert.True(child.Returns);
            Assert.True(child.CannotReturn);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Distance_override_rejects_unadopted_early_end_transitions(bool lobbed)
    {
        var recipe = new ProjectileProfile("synthetic")
            { Lobbed = lobbed, EndEmission = lobbed ? null : new(new("child")) };
        Assert.Throws<InvalidOperationException>(() => Resolve(recipe, Stats(("projectile_distance_override", 40))));
    }

    [Fact]
    public void Distance_override_updates_an_ordinary_profile()
    {
        Assert.Equal(40, Resolve(new("synthetic"), Stats(("projectile_distance_override", 40))).Range);
    }
}
