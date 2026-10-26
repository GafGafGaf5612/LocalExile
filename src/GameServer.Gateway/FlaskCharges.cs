/// <summary>
/// A flask's charges with its mods and the character's (Agents/docs/protocol/13-items.md §8b): how many a use takes and
/// how many it holds. Path of Building Item.lua: charges used = floor(base × (1 + the flask's local_charges_used_+%)),
/// then the character's flask_charges_used_+% multiplies it; maximum = base + local_extra_max_charges.
/// </summary>
static class FlaskCharges
{
    /// <summary>The flask's charges per use and maximum with its local mods (<paramref name="local"/>, by Stats.dat Id)
    /// and the character's stats (<paramref name="character"/>; null: none, e.g. for refills, which only need the
    /// maximum).</summary>
    public static Flasks.Flask Of(Flasks.Flask type, IReadOnlyDictionary<string, int> local, Func<string, int>? character = null)
    {
        int perUse = (int)Math.Floor(type.ChargesPerUse * (100 + local.GetValueOrDefault("local_charges_used_+%")) / 100.0);
        perUse = (int)Math.Floor(perUse * (100 + (character?.Invoke("flask_charges_used_+%") ?? 0)) / 100.0);
        return type with
        {
            ChargesPerUse = Math.Max(0, perUse),
            MaxCharges = Math.Max(1, type.MaxCharges + local.GetValueOrDefault("local_extra_max_charges")),
        };
    }
}
