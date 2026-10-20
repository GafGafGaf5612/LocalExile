using GameServer.Core.World;

/// <summary>
/// Impale, as the game does it (the mechanic behind issue #75's Chance to Impale row): a physical hit that rolls impale
/// stores 10% of that hit's physical damage on the target, up to five stacks. Every following physical hit on that
/// target takes the stored damage again as extra physical, for five hits per stack — so five stacks add half of the
/// impaling hit's physical damage to each hit while they last, the game's 10% more damage per impale.
/// Only the physical part is stored: impale carries no elemental or chaos damage. The roll reads the same
/// <c>impale_on_hit_%_chance</c> stat the panel row shows, so /set_stat moves both.
/// </summary>
sealed class ImpaleState
{
    /// <summary>The game's default: five impales, each lasting five hits.</summary>
    public const int MaxStacks = 5, HitsPerStack = 5;
    /// <summary>Share of the hit's physical damage one impale stores.</summary>
    public const double StoredShare = 0.10;

    readonly List<(double Stored, int HitsLeft)> _stacks = [];

    public int Stacks => _stacks.Count;

    /// <summary>The extra physical damage the next physical hit on this target takes.</summary>
    public double Bonus => _stacks.Sum(s => s.Stored);

    /// <summary>Stores 10% of the hit's physical damage as a new impale, unless the target already carries five.</summary>
    public bool Apply(double physicalDamage)
    {
        if (physicalDamage <= 0 || _stacks.Count >= MaxStacks) return false;
        _stacks.Add((physicalDamage * StoredShare, HitsPerStack));
        return true;
    }

    /// <summary>
    /// A physical hit has landed, so every impale already on the target lasts one hit fewer; expired ones drop off.
    /// Call this before <see cref="Apply"/> for the same hit, or the new stack would lose one of its five hits.
    /// </summary>
    public void AfterHit()
    {
        for (int i = _stacks.Count - 1; i >= 0; i--)
        {
            var stack = _stacks[i];
            if (stack.HitsLeft <= 1) _stacks.RemoveAt(i);
            else _stacks[i] = (stack.Stored, stack.HitsLeft - 1);
        }
    }

    public void Clear() => _stacks.Clear();
}
