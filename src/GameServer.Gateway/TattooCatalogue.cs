using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The client's tattoo items: BaseItemTypes rows whose name contains "Tattoo", joined to
/// <c>PassiveSkillTattoos</c> for the target set and tribe. The effect itself is the replacement passive; the
/// currency item carries no mods. Runegrafts share the apply action and are excluded by the name filter.
/// </summary>
static class TattooCatalogue
{
    public sealed record Entry(int Row, string Id, string Name, int Set, string Target, int Tribe, int Override,
        int OverrideHash, bool Forbidden)
    {
        public bool DropDisabled => Name.StartsWith("Honoured ", StringComparison.Ordinal)
            || Name.StartsWith("Loyalty ", StringComparison.Ordinal)
            || Name.StartsWith("Ancestral ", StringComparison.Ordinal)
            || Name.Contains(" Makanga", StringComparison.Ordinal)
            || Name == "Journey Tattoo of Makanui";

        public bool EventOnly => Forbidden;
    }

    static readonly Lazy<List<Entry>> Entries = new(Load);

    public static IReadOnlyList<Entry> All => Entries.Value;

    public static string[] Names => [.. All.Select(entry => entry.Name)];

    public static Entry? Find(string query)
    {
        if (All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        return All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates one tattoo currency item. The client reads the effect from its own tattoo data; the item is
    /// just the BaseItemTypes row with a stack size of one.</summary>
    public static Item? Create(ItemCatalog items, string query)
    {
        if (Find(query) is not { } entry || items.Find(entry.Id) is not { } baseType) return null;
        return ItemCatalog.Create(baseType) with { StackSize = 1 };
    }

    static List<Entry> Load()
    {
        var result = new List<Entry>();
        string basePath = Path.Combine("data", "tables", "BaseItemTypes.json");
        string tattooPath = Path.Combine("data", "tables", "PassiveSkillTattoos.json");
        string setPath = Path.Combine("data", "tables", "PassiveSkillTattooTargetSets.json");
        string overridePath = Path.Combine("data", "tables", "PassiveSkillOverrides.json");
        if (!File.Exists(basePath) || !File.Exists(tattooPath) || !File.Exists(setPath)) return result;

        var overrideHashes = new List<int>();
        if (File.Exists(overridePath))
            using (var document = JsonDocument.Parse(File.ReadAllBytes(overridePath)))
                foreach (var row in document.RootElement.EnumerateArray())
                    overrideHashes.Add(row.GetProperty("HASH16").GetInt32());

        var sets = new List<string>();
        using (var document = JsonDocument.Parse(File.ReadAllBytes(setPath)))
            foreach (var row in document.RootElement.EnumerateArray())
            {
                string name = row.GetProperty("Name").GetString() ?? "";
                string qualifier = row.GetProperty("Qualifier").GetString() ?? "";
                sets.Add(qualifier.Length > 0 ? $"{name} ({qualifier})" : name);
            }

        var targets = new Dictionary<int, (int Set, int Tribe, int Override)>();
        using (var document = JsonDocument.Parse(File.ReadAllBytes(tattooPath)))
            foreach (var row in document.RootElement.EnumerateArray())
            {
                int item = row.GetProperty("Tattoo").GetInt32();
                targets[item] = (row.GetProperty("Set").GetInt32(), row.GetProperty("Tribe").GetInt32(),
                    row.GetProperty("Override").GetInt32());
            }

        using var bases = JsonDocument.Parse(File.ReadAllBytes(basePath));
        int index = 0;
        foreach (var row in bases.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("Name").GetString() ?? "";
            string id = row.GetProperty("Id").GetString() ?? "";
            if (name.Contains("Tattoo", StringComparison.Ordinal))
            {
                if (!targets.TryGetValue(index, out var target)) continue;
                string targetName = target.Set >= 0 && target.Set < sets.Count ? sets[target.Set] : "unknown";
                bool forbidden = name.StartsWith("Forbidden Tattoo", StringComparison.Ordinal);
                int overrideHash = target.Override >= 0 && target.Override < overrideHashes.Count
                    ? overrideHashes[target.Override] : 0;
                result.Add(new Entry(index, id, name, target.Set, targetName, target.Tribe, target.Override,
                    overrideHash, forbidden));
            }
            index++;
        }
        return result;
    }
}
