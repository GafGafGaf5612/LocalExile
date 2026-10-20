using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

/// <summary>
/// Reads the damage-conversion and gain-as-extra stats the client already has. The stat names follow Stats.dat:
/// <c>physical_damage_%_to_convert_to_fire</c>, <c>base_physical_damage_%_to_convert_to_cold</c>,
/// <c>attack_physical_damage_%_to_add_as_lightning</c>, and so on. Skill, base and global conversion stack; the
/// resulting percentages are normalised by <see cref="DamageConversion"/>.
/// </summary>
static class DamageStats
{
    private static readonly DamageType[] Types =
        [DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos];

    private static string Slug(DamageType type) => type switch
    {
        DamageType.Physical => "physical",
        DamageType.Lightning => "lightning",
        DamageType.Cold => "cold",
        DamageType.Fire => "fire",
        DamageType.Chaos => "chaos",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Conversion for a hit. <paramref name="attack"/> selects the attack-only stats; spells use the spell
    /// stats instead. Conditional stats (while affected by an aura, against a frozen enemy, …) are ignored.</summary>
    public static DamageConversion Conversion(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog, bool attack)
    {
        var conversion = new DamageConversion();
        int Get(string name) => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Add(string name, DamageType from, DamageType to, bool skill = false)
        {
            int percent = Get(name);
            if (percent > 0)
            {
                if (skill) conversion.AddSkill(from, to, percent);
                else conversion.AddGlobal(from, to, percent);
            }
        }
        void Gain(string name, DamageType from, DamageType to)
        {
            int percent = Get(name);
            if (percent > 0) conversion.AddGain(from, to, percent);
        }

        for (int fromIndex = 0; fromIndex < Types.Length - 1; fromIndex++)
        {
            var from = Types[fromIndex];
            string source = Slug(from);
            for (int toIndex = fromIndex + 1; toIndex < Types.Length; toIndex++)
            {
                var to = Types[toIndex];
                string target = Slug(to);
                Add($"{source}_damage_%_to_convert_to_{target}", from, to);
                Add($"base_{source}_damage_%_to_convert_to_{target}", from, to);
                Add($"skill_{source}_damage_%_to_convert_to_{target}", from, to, skill: true);
                Add($"{(attack ? "attack" : "spell")}_{source}_damage_%_to_convert_to_{target}", from, to);
                Gain($"{source}_damage_%_to_add_as_{target}", from, to);
                Gain($"skill_{source}_damage_%_to_add_as_{target}", from, to);
                if (from == DamageType.Physical)
                    Gain($"{(attack ? "attack" : "spell")}_physical_damage_%_to_add_as_{target}", from, to);
            }
        }

        // Non-chaos to chaos is a common wording (e.g. "gain 15% of non-chaos damage as extra chaos damage").
        int nonChaos = Get("non_chaos_damage_to_add_as_chaos_damage_%");
        if (nonChaos > 0)
            foreach (var type in Types)
                if (type != DamageType.Chaos) conversion.AddGain(type, DamageType.Chaos, nonChaos);
        int elementalChaos = Get("elemental_damage_%_to_add_as_chaos");
        if (elementalChaos > 0)
            foreach (var type in Types)
                if (DamageTypes.IsElemental(type)) conversion.AddGain(type, DamageType.Chaos, elementalChaos);

        return conversion;
    }
}
