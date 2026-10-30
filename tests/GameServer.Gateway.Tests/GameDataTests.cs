using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class GameDataTests
{
    [Fact]
    public void A_mod_range_override_changes_new_rolls_and_max_values()
    {
        var ranges = new ModRangeProvider();
        var catalog = new ModCatalog("data", ranges: ranges);
        var mod = catalog.ById("LocalAddedPhysicalDamage9");
        Assert.NotNull(mod);
        Assert.Equal(2, mod.Stats.Count);
        int baseMin = mod.Stats[0].Min, baseMax = mod.Stats[1].Max;

        ranges.Replace(new Dictionary<(string, int), (int, int)>
        {
            [(mod.Id, 0)] = (100, 100),
            [(mod.Id, 1)] = (200, 200),
        });

        var rolled = mod.Roll(ModSection.Explicit, new Random(1));
        Assert.Equal([100, 200], rolled.Values);
        Assert.Equal([100, 200], mod.MaxValues());
        // The reference row is unchanged; only the effective provider is overridden.
        Assert.Equal(baseMin, mod.Stats[0].Min);
        Assert.Equal(baseMax, mod.Stats[1].Max);
    }

    [Fact]
    public void A_mod_range_provider_normalises_a_reversed_range()
    {
        var ranges = new ModRangeProvider();
        ranges.Replace(new Dictionary<(string, int), (int, int)> { [("x", 0)] = (20, 10) });
        Assert.Equal((10, 20), ranges.Range("x", 0, 1, 2));
        Assert.Equal((1, 2), ranges.Range("missing", 0, 1, 2));
    }

    [Fact]
    public void Limit_settings_use_overrides_and_zero_means_unlimited()
    {
        var limits = new LimitSettings();
        Assert.Equal(1, limits.Get("timeless_jewel", 1));
        Assert.Equal(5, limits.Get("unknown", 5));

        limits.Replace(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["timeless_jewel"] = 2,
            ["impossible_escape"] = 0,
        });
        Assert.Equal(2, limits.Get("TIMELESS_JEWEL", 1));
        Assert.Equal(0, limits.Get("impossible_escape", 1));
        Assert.Equal(5, limits.Get("unknown", 5));
    }

    [Fact]
    public void The_game_data_store_round_trips_ranges_and_limits()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"localexile-game-data-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new GameDataStore(path))
            {
                store.SetModRange("LocalAddedPhysicalDamage9", 0, 100, 200);
                store.SetLimit("timeless_jewel", 2);
                Assert.Equal("1", store.Meta("schema_version"));
                Assert.Equal([new GameDataStore.ModStatOverride("LocalAddedPhysicalDamage9", 0, 100, 200)],
                    store.LoadModRanges());
                Assert.Equal([new GameDataStore.LimitOverride("timeless_jewel", 2)], store.LoadLimits());
            }

            using (var reopened = new GameDataStore(path))
            {
                Assert.Single(reopened.LoadModRanges());
                Assert.Single(reopened.LoadLimits());
                reopened.ClearModRange("LocalAddedPhysicalDamage9");
                reopened.ClearLimit("timeless_jewel");
                Assert.Empty(reopened.LoadModRanges());
                Assert.Empty(reopened.LoadLimits());
            }
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_game_data_store_round_trips_catalog_rows_and_keeps_a_seeded_flag()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"localexile-catalog-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new GameDataStore(path))
            {
                Assert.False(store.CatalogSeeded("radius_jewel"));
                store.PutCatalogRow("radius_jewel", "Thread of Hope|Crimson Jewel", "{\"Name\":\"Thread of Hope\"}");
                store.PutCatalogRow("radius_jewel", "Disabled|Crimson Jewel", "{\"Name\":\"Disabled\"}", enabled: false);
                store.MarkCatalogSeeded("radius_jewel");
                Assert.True(store.CatalogSeeded("radius_jewel"));
                var rows = store.LoadCatalog("radius_jewel");
                Assert.Equal(2, rows.Count);
                Assert.False(rows.Single(row => row.Key == "Disabled|Crimson Jewel").Enabled);
                store.DeleteCatalogRow("radius_jewel", "Thread of Hope|Crimson Jewel");
                store.DeleteCatalogRow("radius_jewel", "Disabled|Crimson Jewel");
                Assert.Empty(store.LoadCatalog("radius_jewel"));
                Assert.True(store.CatalogSeeded("radius_jewel"));
            }
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_catalog_rows_round_trip_through_the_json_payload_shape()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var radius = RadiusJewelCatalogue.All.First(entry => entry.Name == "Thread of Hope");
        var radiusBack = JsonSerializer.Deserialize<RadiusJewelCatalogue.Entry>(JsonSerializer.Serialize(radius, options), options)!;
        Assert.Equal(radius.Name, radiusBack.Name);
        Assert.Equal(radius.Base, radiusBack.Base);
        Assert.Equal(radius.ConversionMod, radiusBack.ConversionMod);
        Assert.Equal(radius.Limit, radiusBack.Limit);
        Assert.Equal(radius.Radius, radiusBack.Radius);
        Assert.Equal(radius.GlobalMods, radiusBack.GlobalMods);
        Assert.Equal(radius.Variants!.Select(v => (v.Name, v.ModId, v.Value)), radiusBack.Variants!.Select(v => (v.Name, v.ModId, v.Value)));

        var timeless = TimelessJewelCatalog.All[0];
        var timelessBack = JsonSerializer.Deserialize<TimelessJewelCatalog.Entry>(
            JsonSerializer.Serialize(timeless, options), options)!;
        Assert.Equal(timeless.Name, timelessBack.Name);
        Assert.Equal(timeless.TreeVersion, timelessBack.TreeVersion);
        Assert.Equal(timeless.MinSeed, timelessBack.MinSeed);
        Assert.Equal(timeless.Conquerors.Select(c => (c.Name, c.Index, c.Version)),
            timelessBack.Conquerors.Select(c => (c.Name, c.Index, c.Version)));

        var unique = UniqueCatalog.Seed[0];
        var uniqueBack = JsonSerializer.Deserialize<UniqueCatalog.Definition>(
            JsonSerializer.Serialize(unique, options), options)!;
        Assert.Equal(unique.Name, uniqueBack.Name);
        Assert.Equal(unique.BaseTypeId, uniqueBack.BaseTypeId);
        Assert.Equal(unique.ItemLevel, uniqueBack.ItemLevel);
        Assert.Equal(unique.Mods, uniqueBack.Mods);
    }

    [Fact]
    public void A_tattoo_group_limit_can_be_raised_without_a_restart()
    {
        var warlord = TattooCatalogue.Find("Honoured Tattoo of the Warlord")!;
        var mountain = TattooCatalogue.Find("Honoured Tattoo of the Mountain")!;
        var tattoos = new Dictionary<ushort, string> { [100] = warlord.Name };

        Assert.Equal("only one Attribute Notable Tattoo can be applied",
            Program.TattooLimitRefusal(tattoos, 200, mountain));

        var limits = new LimitSettings();
        limits.Replace(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["tattoo:Attribute Notable Tattoo"] = 2,
        });
        Assert.Null(Program.TattooLimitRefusal(tattoos, 200, mountain, limits));
    }
}
