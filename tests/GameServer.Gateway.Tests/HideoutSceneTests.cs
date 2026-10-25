using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class HideoutSceneTests
{
    [Fact]
    public void Choice_is_shared_with_auth_and_preserves_character_progress()
    {
        TestSaves.Ensure();
        string name = "Home" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(name, new CharacterSaves.Save([], Experience: 12345, ActiveAuras: ["Grace"]));
        CharacterSaves.SetSelectedHideout(name, "HideoutShapersRealm");
        Assert.Equal("HideoutShapersRealm", CharacterHome.SelectedHideout(Saves.Db, name));
        Assert.Equal(12345u, CharacterSaves.Load(name)!.Experience);
        Assert.Equal(new[] { "Grace" }, CharacterSaves.Load(name)!.ActiveAuras);
        CharacterSaves.SetSelectedHideout(name, "HideoutForest");
        Assert.Equal("HideoutForest", CharacterHome.SelectedHideout(Saves.Db, name));
        Assert.Null(CharacterHome.SelectedHideout(Saves.Db, name + "other"));
    }

    [Fact]
    public void Furniture_and_npcs_use_the_new_floor_and_preserve_their_appearance()
    {
        HideoutCatalog.Doodad D(string type, int x, int y) => new(type, 123, type, "model.ao", x, y, 1.5f, 2);
        var fallback = new HideoutCatalog.Hideout(0, 1, HideoutCatalog.DefaultArea, 1, "Lush",
            [D(WaypointObject.MetadataPath, 218, 335), D(StashObject.MetadataPath, 230, 364),
             D(HideoutCatalog.CraftingBenchType, 251, 351), D("TestMappingDevice", 257, 283)]);
        var target = fallback with { Row = 29, AreaId = "Target", Doodads =
            [D(WaypointObject.MetadataPath, 500, 500), D(StashObject.MetadataPath, 510, 520)] };
        bool Floor(int x, int y) => x >= 470 && x <= 570 && y >= 470 && y <= 570 && !(x >= 520 && y >= 520);
        var scene = HideoutScene.Build(target, fallback, Floor);
        Assert.Equal(NpcCatalog.HideoutNpcs.Count, scene.Npcs.Count);
        Assert.Equal(fallback.First(HideoutCatalog.CraftingBenchType)!.Hash, scene.Bench.Hash);
        Assert.Equal("model.ao", scene.Device.Model);
        var points = new List<(int X, int Y)> { (scene.Stash.X, scene.Stash.Y), scene.Guild,
            (scene.Bench.X, scene.Bench.Y), (scene.Device.X, scene.Device.Y) };
        points.AddRange(scene.Npcs.Select(n => (n.X, n.Y)));
        Assert.Equal(points.Count, points.Distinct().Count());
        Assert.All(points, p => Assert.True(Floor(p.X, p.Y)));
        Assert.All(scene.Npcs, n => Assert.True(n.X > 450 && n.Y > 450));
    }
}
