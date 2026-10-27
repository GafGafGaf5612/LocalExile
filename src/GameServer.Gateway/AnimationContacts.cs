using System.Text.Json;

/// <summary>
/// The "contact" keyframe of the character classes' animations (data/animation-contacts.json, written by
/// <c>python -I scripts/re/anim_contacts.py</c> from each class's rig .amd): per animated object, per animation, its length
/// and the contact time in ms. A spell is released at the contact, which the client scales with the cast time, so the
/// server releases it at the same fraction of the cast (Witch spell_projectile_wand: 599 of 1066 ms).
/// Ordinary wand attacks additionally use the class ACT's ordered stance pool and the native action-seeded selector.
/// </summary>
sealed class AnimationContacts(string folder = "data")
{
    /// <summary>The Witch's spell_projectile fraction, used when the file or the animation is missing.</summary>
    public const double DefaultSpellRelease = 599.0 / 1066;

    /// <summary>The default attack's contact fraction when the class's animation or weapon stance is missing
    /// (attack1_unarmed 333/833 = 0.4 is representative).</summary>
    public const double DefaultAttackContact = 0.4;

    private sealed record ActorAnimations(Dictionary<string, (int Length, int Contact)> Clips,
        Dictionary<string, DefaultWandContact[]> WandPools);

    private readonly Lazy<Dictionary<string, ActorAnimations>> _byAo = new(() => Load(folder));

    /// <summary>When in a spell cast its projectile leaves, as a fraction of the cast: the class's spell_projectile
    /// animation (wand, unarmed, then any), else any spell animation's contact.</summary>
    public double SpellRelease(string animatedObject)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return DefaultSpellRelease;
        var anims = actor.Clips;
        foreach (string name in (string[])["spell_projectile_wand", "spell_projectile_unarmed"])
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return (double)a.Contact / a.Length;
        foreach (string prefix in (string[])["spell_projectile", "spell_"])
            foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (name.StartsWith(prefix, StringComparison.Ordinal) && a.Length > 0) return (double)a.Contact / a.Length;
        return DefaultSpellRelease;
    }

    /// <summary>When a melee attack's weapon visually hits (the attack1 animation's contact keyframe), as a fraction of the
    /// attack: the class's <c>attack1_&lt;weapon&gt;</c> clip, then its shield/dual-wield variant, then any attack1 of that
    /// weapon. The client stretches the clip to the attack time, so the server pays the hit at this fraction of it.</summary>
    public double AttackContact(string animatedObject, WeaponProfile weapon)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return DefaultAttackContact;
        var anims = actor.Clips;
        string? main = weapon.Unarmed ? "unarmed" : weapon.Bow ? "bow" : weapon.Staff ? "staff"
            : weapon.Sword && weapon.TwoHandMelee ? "2hsword" : weapon.Mace && weapon.TwoHandMelee ? "maul"
            : weapon.Sword ? "sword" : weapon.Dagger ? "dagger" : weapon.Claw ? "claw" : weapon.Wand ? "wand" : null;
        if (main is null) return DefaultAttackContact;
        string[] candidates = weapon.Shield
            ? [$"attack1_{main}_shield", $"attack1_{main}"]
            : weapon.DualWield
            ? [$"attack1_{main}_{main}", $"attack1_{main}"]
            : [$"attack1_{main}"];
        foreach (string name in candidates)
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return (double)a.Contact / a.Length;
        foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (name.StartsWith($"attack1_{main}", StringComparison.Ordinal) && a.Length > 0) return (double)a.Contact / a.Length;
        return DefaultAttackContact;
    }

    /// <summary>
    /// The ordinary wand's ACT-selected main-hand clip, seeded by a fresh action's zero-extended sequence.
    /// Missing old/generated data returns false so the caller can identify approximate fallback timing.
    /// ACT pools exported here are unconditional, group-zero, weight-one entries; no conditional selection is guessed.
    /// </summary>
    public bool TryDefaultWandContact(string animatedObject, WeaponProfile weapon, ushort sequence,
        out DefaultWandContact selection)
    {
        selection = default;
        if (!weapon.Wand || !_byAo.Value.TryGetValue(animatedObject, out var actor)) return false;
        string stance = weapon.Shield ? "WandShield" : weapon.DualWield ? "WandWand" : "Wand";
        if (!actor.WandPools.TryGetValue(stance, out var pool)) return false;
        selection = pool[AnimationVariantSelector.Select(sequence, pool.Select(a => a.Weight).ToArray())];
        return true;
    }

    private static Dictionary<string, ActorAnimations> Load(string folder)
    {
        string path = Path.Combine(folder, "animation-contacts.json");
        var result = new Dictionary<string, ActorAnimations>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var ao in d.RootElement.EnumerateObject())
        {
            var clips = ao.Value.GetProperty("animations").EnumerateObject()
                .ToDictionary(a => a.Name, a => (a.Value[0].GetInt32(), a.Value[1].GetInt32()));
            var pools = new Dictionary<string, DefaultWandContact[]>(StringComparer.Ordinal);
            if (ao.Value.TryGetProperty("defaultWand", out var wand))
                foreach (var stance in wand.EnumerateObject())
                {
                    var entries = stance.Value.EnumerateArray().Select(a => new DefaultWandContact(
                        a.GetProperty("animation").GetString()!, a.GetProperty("index").GetUInt32(),
                        a.GetProperty("length").GetInt32(), a.GetProperty("contact").GetInt32(),
                        stance.Name, a.GetProperty("weight").GetUInt32())).ToArray();
                    if (entries.Length == 0 || entries.Any(a => string.IsNullOrWhiteSpace(a.Animation)
                        || a.LengthMs <= 0 || a.ContactMs < 0 || a.ContactMs > a.LengthMs || a.Weight != 1))
                        throw new InvalidDataException($"Invalid ordinary wand animation pool {ao.Name}/{stance.Name}.");
                    pools.Add(stance.Name, entries);
                }
            result[ao.Name] = new ActorAnimations(clips, pools);
        }
        return result;
    }
}

/// <summary>A rig clip chosen from the ordinary wand ACT pool, including its contact keyframe.</summary>
readonly record struct DefaultWandContact(string Animation, uint AnimationIndex, int LengthMs, int ContactMs,
    string Stance, uint Weight)
{
    public double ContactFraction => (double)ContactMs / LengthMs;
}
