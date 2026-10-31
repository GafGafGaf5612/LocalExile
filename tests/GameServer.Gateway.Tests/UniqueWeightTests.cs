using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// Unique drop weights: seeded reference rows from VillageUniqueDisenchantValues, admin overrides on top, and the
// provider ItemDrops reads. The extracted JSON is the fallback only until the reference table exists.
public class UniqueWeightTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/BaseItemTypes.json")))
                return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("BaseItemTypes.json");
    }

    static readonly Lazy<(ItemCatalog Catalog, string Data, UniqueCatalog Uniques)> Setup = new(() =>
    {
        string data = Data();
        var catalog = new ItemCatalog(data);
        return (catalog, data, new UniqueCatalog(catalog, data));
    });

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"localexile-weights-{Guid.NewGuid():N}.db");

    static void Delete(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public void The_reference_seeds_from_the_client_table_and_a_unique_uses_its_weight()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            Assert.True(GameDataWeights.Seed(store));
            Assert.False(GameDataWeights.Seed(store));
            Assert.Equal(1527, store.UniqueWeightCounts().Weights);

            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.True(provider.HasTable);
            Assert.True(provider.Weight("Wanderlust", false) > provider.Weight("Mageblood", false));
            Assert.Equal(0, provider.Weight("Victario's Flight", false));

            var (catalog, data, uniques) = Setup.Value;
            var drops = new ItemDrops(catalog, new ItemRolls(catalog, data), data,
                uniques: uniques, uniqueWeights: provider);
            Assert.Equal(provider.Weight("Mageblood", false), drops.UniqueWeight("Mageblood"));
            Assert.Equal(0, drops.UniqueWeight("Victario's Flight"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_empty_seeded_reference_does_not_fall_back_to_the_json()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            GameDataWeights.Seed(store);
            store.SeedUniqueWeights([]);
            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.True(provider.HasTable);
            Assert.Equal(0, provider.Weight("Mageblood", false));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_override_replaces_the_reference_and_clear_restores_it()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            GameDataWeights.Seed(store);
            double reference = store.LoadUniqueWeights().Single(row => row.Name == "Mageblood").Weight;

            store.SetUniqueWeight("Mageblood", 0.5, 0.25);
            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.Equal(0.5, provider.Weight("Mageblood", false));
            Assert.Equal(0.25, provider.Weight("Mageblood", true));
            Assert.Equal((1527, 1), store.UniqueWeightCounts());

            store.ClearUniqueWeight("Mageblood");
            GameDataWeights.Reload(store, provider);
            Assert.Equal(reference, provider.Weight("Mageblood", false));
            Assert.Equal((1527, 0), store.UniqueWeightCounts());
        }
        finally
        {
            Delete(path);
        }
    }
}
