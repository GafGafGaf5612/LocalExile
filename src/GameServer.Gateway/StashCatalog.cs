/// <summary>
/// Where a town's Stash stands (Agents/docs/protocol/18-stash.md §1). The client files do not place it (Stash.ot has only
/// its model; the town tiles' .tmd records hold the waypoint and doodads), so the cells are ours, as for the NPCs.
/// </summary>
static class StashCatalog
{
    public sealed record Placement(int X, int Y, float Rotation);

    static readonly Dictionary<ushort, Placement> Towns = new()
    {
        // Lioneye's Watch: next to Nessa (346, 271), the place the user picked (2026-10-19).
        [AreaLayouts.LioneyesWatch] = new(319, 272, 0),
    };

    public static Placement? In(ushort areaHash) => Towns.GetValueOrDefault(areaHash);
}
