using System.Text.Json;

/// <summary>
/// The "contact" keyframe of the character classes' animations (data/animation-contacts.json, written by
/// <c>python -I scripts/re/anim_contacts.py</c> from each class's rig .amd): per animated object, per animation, its length
/// and the contact time in ms. A spell is released at the contact, which the client scales with the cast time, so the
/// server releases it at the same fraction of the cast (Witch spell_projectile_wand: 599 of 1066 ms).
/// </summary>
sealed class AnimationContacts(string folder = "data")
{
    /// <summary>The Witch's spell_projectile fraction, used when the file or the animation is missing.</summary>
    public const double DefaultSpellRelease = 599.0 / 1066;

    private readonly Lazy<Dictionary<string, Dictionary<string, (int Length, int Contact)>>> _byAo = new(() => Load(folder));

    /// <summary>When in a spell cast its projectile leaves, as a fraction of the cast: the class's spell_projectile
    /// animation (wand, unarmed, then any), else any spell animation's contact.</summary>
    public double SpellRelease(string animatedObject)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var anims)) return DefaultSpellRelease;
        foreach (string name in (string[])["spell_projectile_wand", "spell_projectile_unarmed"])
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return (double)a.Contact / a.Length;
        foreach (string prefix in (string[])["spell_projectile", "spell_"])
            foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (name.StartsWith(prefix, StringComparison.Ordinal) && a.Length > 0) return (double)a.Contact / a.Length;
        return DefaultSpellRelease;
    }

    private static Dictionary<string, Dictionary<string, (int, int)>> Load(string folder)
    {
        string path = Path.Combine(folder, "animation-contacts.json");
        var result = new Dictionary<string, Dictionary<string, (int, int)>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var ao in d.RootElement.EnumerateObject())
            result[ao.Name] = ao.Value.GetProperty("animations").EnumerateObject()
                .ToDictionary(a => a.Name, a => (a.Value[0].GetInt32(), a.Value[1].GetInt32()));
        return result;
    }
}
