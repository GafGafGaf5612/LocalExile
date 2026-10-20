using System.Text.Json;

namespace GameServer.Gateway.Tests;

/// <summary>
/// One save database for the whole test run (<see cref="Saves"/> is static, and test classes run in parallel); every test
/// uses its own character and account names. Also writes data folders whose item tables know a few types, so saves
/// holding them load again (a saved item the game data doesn't know stops the load).
/// </summary>
static class TestSaves
{
    /// <summary>The test classes that set Saves.FailWrite (static) are in this collection: they never run at once.</summary>
    public const string FailWriteCollection = "Saves.FailWrite";

    static readonly Lock Gate = new();
    static bool _open;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_open) return;
            string path = Path.Combine(Path.GetTempPath(), $"localexile-tests-{Guid.NewGuid():N}.db");
            Saves.Open(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Saves.Db.Dispose();
                foreach (string f in new[] { path, path + "-wal", path + "-shm" })
                    try { File.Delete(f); } catch (IOException) { }
            };
            _open = true;
        }
    }

    /// <summary>A type the test catalog knows: its HASH32, Id, size, and whether it stacks (CurrencyItems StackSize).</summary>
    public readonly record struct Type(uint Hash, string Id, byte Width = 1, byte Height = 1, ushort StackSize = 1);

    /// <summary>A new data folder (deleted by the caller) whose tables know these types (BaseItemTypes rows in order).</summary>
    public static string Catalog(params Type[] types)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        void Write(string file, object value) => File.WriteAllText(Path.Combine(folder, file), JsonSerializer.Serialize(value));
        Write("tables/BaseItemTypes.json", types.Select(t => new Dictionary<string, object?>
        {
            ["Id"] = t.Id, ["Name"] = t.Id.Split('/')[^1], ["HASH32"] = t.Hash, ["Width"] = t.Width, ["Height"] = t.Height,
            ["ItemVisualIdentity"] = 0, ["ItemClassesKey"] = null, ["ItemStance"] = null, ["DropLevel"] = 1,
            ["Implicit_ModsKeys"] = Array.Empty<int>(), ["TagsKeys"] = Array.Empty<int>(), ["ModDomain"] = 1,
        }));
        Write("tables/ItemVisualIdentity.json", new[] { new { HASH16 = 1 } });
        Write("tables/CurrencyItems.json", types.Select((t, row) => new { BaseItemTypesKey = row, StackSize = (int)t.StackSize, FullStack_BaseItemTypesKey = (int?)null }));
        Write("item-types.json", new
        {
            types = types.Select(t => new { type = t.Id, components = t.StackSize > 1 ? new[] { "Base", "Stack" } : ["Base"], read = t.StackSize > 1 ? 2 : 1 }),
        });
        return folder;
    }
}
