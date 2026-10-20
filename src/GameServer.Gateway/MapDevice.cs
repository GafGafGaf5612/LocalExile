using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The hideout's map device (Agents/docs/protocol/21-hideouts.md §5): what its slots (Inventories row 13 <c>Map1</c>)
/// may hold when Activate (C→S 0x00d8) is pressed, which area a map opens (Maps.dat <c>Regular_WorldAreasKey</c>), and
/// each character's open map: its area, its instance once someone entered it, and the portals left.
/// </summary>
sealed class MapDevice
{
    /// <summary>Portals one activation opens; each takes one player into the map once.</summary>
    public const int PortalCount = 6;

    /// <summary>ItemClasses Ids the slots take.</summary>
    public const string MapClass = "Map", FragmentClass = "MapFragment";

    /// <summary>The area a map item opens: WorldAreas HASH16, Id and name, and the map's tier.</summary>
    public sealed record MapArea(ushort Hash16, string Id, string Name, int Tier);

    private readonly Dictionary<uint, MapArea> _byItemType;

    public MapDevice(IReadOnlyDictionary<uint, MapArea> byItemType) => _byItemType = new(byItemType);

    /// <summary>Maps.dat: each map's BaseItemTypes row, by its HASH32 (<paramref name="hashOfRow"/>), to its regular area.
    /// Empty when the tables are missing.</summary>
    public static MapDevice Load(string folder, Func<int, uint?> hashOfRow)
    {
        var byType = new Dictionary<uint, MapArea>();
        string maps = Path.Combine(folder, "tables", "Maps.json"), areas = Path.Combine(folder, "tables", "WorldAreas.json");
        if (File.Exists(maps) && File.Exists(areas))
        {
            using var m = JsonDocument.Parse(File.ReadAllBytes(maps));
            using var a = JsonDocument.Parse(File.ReadAllBytes(areas));
            int areaCount = a.RootElement.GetArrayLength();
            foreach (var row in m.RootElement.EnumerateArray())
            {
                if (row.GetProperty("BaseItemTypesKey") is not { ValueKind: JsonValueKind.Number } item
                    || row.GetProperty("Regular_WorldAreasKey") is not { ValueKind: JsonValueKind.Number } area
                    || area.GetInt32() >= areaCount || hashOfRow(item.GetInt32()) is not uint hash) continue;
                var wa = a.RootElement[area.GetInt32()];
                byType.TryAdd(hash, new MapArea((ushort)wa.GetProperty("HASH16").GetInt32(), wa.GetProperty("Id").GetString() ?? "",
                    wa.GetProperty("Name").GetString() ?? "", row.GetProperty("Tier").GetInt32()));
            }
        }
        return new MapDevice(byType);
    }

    public int Count => _byItemType.Count;

    public MapArea? AreaOf(uint itemType) => _byItemType.GetValueOrDefault(itemType);

    /// <summary>One item in the slots: its type hash and ItemClasses Id.</summary>
    public readonly record struct Slot(uint TypeHash, string Class, string Name);

    /// <summary>
    /// What Activate does with these slots: only maps and map fragments may be in them, and exactly one map (fragments
    /// alone open nothing yet). Returns the map's area, or null with the reason the player is told.
    /// </summary>
    public MapArea? Check(IReadOnlyList<Slot> slots, out string why)
    {
        why = "";
        if (slots.FirstOrDefault(s => s.Class is not (MapClass or FragmentClass)) is { Class: not null } wrong)
        {
            why = $"{wrong.Name} cannot be used in the Map Device: only maps and map fragments can.";
            return null;
        }
        var maps = slots.Where(s => s.Class == MapClass).ToList();
        if (maps.Count == 0) { why = "Put a map into the Map Device first."; return null; }
        if (maps.Count > 1) { why = "Only one map can be activated at a time."; return null; }
        if (AreaOf(maps[0].TypeHash) is not { } area) { why = $"{maps[0].Name} opens no known area."; return null; }
        return area;
    }

    /// <summary>A character's opened map: its area, its instance (0 until the first portal is taken) and the portals left.</summary>
    public sealed record OpenMap(MapArea Area, ulong Instance, int PortalsLeft);

    private static readonly ConcurrentDictionary<string, OpenMap> Open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character's open map, or null.</summary>
    public static OpenMap? OpenOf(string character) => Open.GetValueOrDefault(character);

    /// <summary>Opens a map for the character, replacing the one open before (its portals close).</summary>
    public static OpenMap Activate(string character, MapArea area) => Open[character] = new OpenMap(area, 0, PortalCount);

    /// <summary>Takes one portal of the character's open map. Returns the map as it was (its instance to enter, 0 for a
    /// new one) and how many portals are left, or null when none is open.</summary>
    public static (OpenMap Map, int Left)? TakePortal(string character)
    {
        while (Open.TryGetValue(character, out var map))
        {
            if (map.PortalsLeft <= 0) { Open.TryRemove(new(character, map)); return null; }
            var after = map with { PortalsLeft = map.PortalsLeft - 1 };
            if (!Open.TryUpdate(character, after, map)) continue;
            return (map, after.PortalsLeft);
        }
        return null;
    }

    /// <summary>The first portal's transfer created the map's instance: the other portals lead there.</summary>
    public static void Entered(string character, MapArea area, ulong instance)
    {
        if (Open.TryGetValue(character, out var map) && map.Area == area && map.Instance == 0)
            Open.TryUpdate(character, map with { Instance = instance }, map);
    }

    /// <summary>The map's instance is gone (expired): the map closes.</summary>
    public static void Close(string character) => Open.TryRemove(character, out _);

    /// <summary>Where portal <paramref name="index"/> of <paramref name="count"/> stands: on a ring around the device,
    /// starting in front of it (south, +y).</summary>
    public static (int X, int Y) PortalCell(int deviceX, int deviceY, int index, int count = PortalCount, int radius = 16)
    {
        double angle = Math.PI / 2 + 2 * Math.PI * index / Math.Max(count, 1);
        return (deviceX + (int)Math.Round(radius * Math.Cos(angle)), deviceY + (int)Math.Round(radius * Math.Sin(angle)));
    }
}
