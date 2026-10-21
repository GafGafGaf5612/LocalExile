/// <summary>
/// Where new skills go on the skill bar (S→C 0x53, Agents/docs/protocol/11-skills.md §11). A saved bar maps slot →
/// GrantedEffects Id, "Action/&lt;Id&gt;" or "" (<see cref="CharacterSaves.Save.SkillBar"/>). Only empty slots take new
/// skills: the slots the player set stay as they are.
/// </summary>
static class SkillBarLayout
{
    /// <summary>The left mouse button: the default attack on a new bar (the Survivors league: Move).</summary>
    public const int LeftButton = 0;

    /// <summary>The basic Move action as a bar entry (ActionTypes "Move", skill id 0x2909).</summary>
    public const string Move = "Action/Move";

    /// <summary>The slots in the order new skills take them: the right mouse button (2), Q W E R T (3–7), the middle mouse
    /// button (1), then the rest.</summary>
    public static readonly int[] FillOrder = [2, 3, 4, 5, 6, 7, 1, 8, 9, 10, 11, 12];

    /// <summary>
    /// The entries to add to <paramref name="bar"/>: on a new bar (its left mouse button never set) the default attack on
    /// the left mouse button, or with <paramref name="moveOnLeft"/> (the Survivors league) Move there and the default
    /// attack after the gem skills; and each of <paramref name="skills"/> that is nowhere on the bar on the first empty
    /// slot of <see cref="FillOrder"/>. A slot is empty when it was never set, was cleared (""), or
    /// holds a skill <paramref name="has"/> says the character no longer has (its gem left the equipment).
    /// </summary>
    public static List<(int Slot, string Entry)> Fill(IReadOnlyDictionary<int, string> bar, string attack, IEnumerable<string> skills,
        Func<string, bool> has, bool moveOnLeft = false)
    {
        var added = new List<(int Slot, string Entry)>();
        bool fresh = !bar.ContainsKey(LeftButton);
        if (fresh) added.Add((LeftButton, moveOnLeft ? Move : attack));
        bool Empty(int slot) => added.All(a => a.Slot != slot) && (!bar.TryGetValue(slot, out var entry) || entry == "" || !has(entry));
        foreach (var skill in skills.Distinct().Append(fresh && moveOnLeft ? attack : null).OfType<string>().Where(s => !bar.Values.Contains(s)))
            if (FillOrder.FirstOrDefault(Empty, -1) is int slot and >= 0)
                added.Add((slot, skill));
        return added;
    }
}
