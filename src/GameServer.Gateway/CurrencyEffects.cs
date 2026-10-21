using GameServer.Core.Protocol.Instance;

/// <summary>
/// Currency used on an item (C→S 0x71, Agents/docs/protocol/13-items.md §4b): what each handled orb does to the target.
/// </summary>
static class CurrencyEffects
{
    public const string ChromaticOrb = "Metadata/Items/Currency/CurrencyRerollSocketColours";

    // shortcut: the real game's colour weights are not known; each colour weighs its attribute requirement + 12.
    const int ColourBase = 12;

    /// <summary>
    /// Chromatic Orb: every socket gets a new colour, red / green / blue weighted by the base's strength / dexterity /
    /// intelligence requirement + 12 (never white). Gems and links stay. The result differs from the old colours (rerolled
    /// up to 50 times; a single socket always can). Null for an item without sockets.
    /// </summary>
    public static Item? Chromatic(Item item, (int Str, int Dex, int Int) requirements, Random random)
    {
        if (item.Sockets.Count == 0) return null;
        int r = requirements.Str + ColourBase, g = requirements.Dex + ColourBase, b = requirements.Int + ColourBase;
        SocketColour Roll()
        {
            int x = random.Next(r + g + b);
            return x < r ? SocketColour.Red : x < r + g ? SocketColour.Green : SocketColour.Blue;
        }
        IReadOnlyList<ItemSocket> sockets = item.Sockets;
        for (int tries = 0; tries < 50; tries++)
        {
            sockets = [.. item.Sockets.Select(s => s with { Colour = Roll() })];
            if (!sockets.Select(s => s.Colour).SequenceEqual(item.Sockets.Select(s => s.Colour))) break;
        }
        return item with { Sockets = sockets };
    }
}
