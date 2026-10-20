namespace GameServer.Gateway.Tests;

public class MapDeviceTests
{
    static readonly MapDevice.MapArea Beach = new(0x1111, "MapWorldsBeach", "Beach", 1);
    static readonly MapDevice Device = new(new Dictionary<uint, MapDevice.MapArea> { [0xbeac] = Beach });

    static MapDevice.Slot Map(uint type = 0xbeac) => new(type, MapDevice.MapClass, "Beach Map");
    static MapDevice.Slot Fragment() => new(0xf7a6, MapDevice.FragmentClass, "Sacrifice at Midnight");

    [Fact]
    public void One_map_with_fragments_opens_the_maps_area()
    {
        Assert.Equal(Beach, Device.Check([Fragment(), Map(), Fragment()], out string why));
        Assert.Equal("", why);
    }

    [Fact]
    public void Anything_but_maps_and_fragments_is_refused()
    {
        Assert.Null(Device.Check([Map(), new(0x1, "Ring", "Iron Ring")], out string why));
        Assert.Contains("Iron Ring", why);
    }

    [Fact]
    public void Two_maps_are_refused()
    {
        Assert.Null(Device.Check([Map(), Map()], out string why));
        Assert.Contains("one map", why);
    }

    [Fact]
    public void Fragments_alone_or_empty_slots_open_nothing()
    {
        Assert.Null(Device.Check([Fragment()], out _));
        Assert.Null(Device.Check([], out _));
        Assert.Null(Device.Check([Map(0xdead)], out string unknown));
        Assert.Contains("no known area", unknown);
    }

    [Fact]
    public void Map_device_slots_take_only_maps_and_fragments()
    {
        Assert.True(PlayerInventories.Accepts(PlayerInventories.MapDevice, MapDevice.MapClass));
        Assert.True(PlayerInventories.Accepts(PlayerInventories.MapDevice, MapDevice.FragmentClass));
        Assert.False(PlayerInventories.Accepts(PlayerInventories.MapDevice, "Ring"));
    }

    [Fact]
    public void Six_portals_each_taken_once_all_into_the_first_ones_instance()
    {
        string who = $"MapTester{Guid.NewGuid():N}";
        Assert.Null(MapDevice.TakePortal(who, 0));
        MapDevice.Activate(who, Beach);
        var first = MapDevice.TakePortal(who, 4);
        Assert.Equal((0UL, 5), (first!.Value.Map.Instance, first.Value.Left));
        MapDevice.Entered(who, Beach, 42);
        Assert.Null(MapDevice.TakePortal(who, 4));                       // that one is gone
        int left = 4;
        foreach (int index in new[] { 0, 5, 1, 3, 2 })
        {
            var next = MapDevice.TakePortal(who, index);
            Assert.Equal((42UL, left--), (next!.Value.Map.Instance, next.Value.Left));
        }
        Assert.Null(MapDevice.OpenOf(who));
    }

    [Fact]
    public void The_portal_taken_is_the_one_missing_afterwards()
    {
        string who = $"MapTester{Guid.NewGuid():N}";
        MapDevice.Activate(who, Beach);
        MapDevice.TakePortal(who, 4);
        Assert.Equal([0, 1, 2, 3, 5], MapDevice.OpenOf(who)!.Left);
    }

    [Fact]
    public void A_new_activation_replaces_the_open_map()
    {
        string who = $"MapTester{Guid.NewGuid():N}";
        MapDevice.Activate(who, Beach);
        MapDevice.TakePortal(who, 2);
        MapDevice.Entered(who, Beach, 7);
        var again = MapDevice.Activate(who, Beach);
        Assert.Equal((0UL, MapDevice.PortalCount), (again.Instance, again.PortalsLeft));
    }

    [Fact]
    public void Portals_stand_on_a_ring_around_the_device_starting_in_front()
    {
        var cells = Enumerable.Range(0, MapDevice.PortalCount).Select(i => MapDevice.PortalCell(100, 100, i)).ToList();
        Assert.Equal((100, 116), cells[0]);
        Assert.Equal(MapDevice.PortalCount, cells.Distinct().Count());
        Assert.All(cells, c => Assert.InRange(Math.Sqrt((c.X - 100) * (c.X - 100) + (c.Y - 100) * (c.Y - 100)), 15, 17));
    }

    [Fact]
    public void Maps_dat_links_map_items_to_their_regular_area()
    {
        string dir = Directory.CreateTempSubdirectory("maps").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "tables"));
            File.WriteAllText(Path.Combine(dir, "tables", "Maps.json"), """
                [{"BaseItemTypesKey":5,"Regular_WorldAreasKey":1,"Tier":3},{"BaseItemTypesKey":6,"Regular_WorldAreasKey":9,"Tier":1}]
                """);
            File.WriteAllText(Path.Combine(dir, "tables", "WorldAreas.json"), """
                [{"Id":"1_1_1","Name":"The Twilight Strand","HASH16":1},{"Id":"MapWorldsBeach","Name":"Beach","HASH16":4660}]
                """);
            var device = MapDevice.Load(dir, row => row == 5 ? 0xabcu : null);
            Assert.Equal(1, device.Count);
            Assert.Equal(new MapDevice.MapArea(0x1234, "MapWorldsBeach", "Beach", 3), device.AreaOf(0xabc));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_maps_area_gets_the_maps_own_monster_packs()
    {
        string dir = Directory.CreateTempSubdirectory("mappacks").FullName;
        try
        {
            string t = Path.Combine(dir, "tables");
            Directory.CreateDirectory(t);
            File.WriteAllText(Path.Combine(t, "MonsterPacks.json"), """
                [{"Id":"Act1-CoastCrabs","MinCount":3,"MaxCount":5,"BossMonsterSpawnChance":0,"BossMonsterCount":0,"BossMonster_MonsterVarietiesKeys":[],"WorldAreasKeys":[0],"__col9":[100]},
                 {"Id":"atlas-crab1","MinCount":6,"MaxCount":9,"BossMonsterSpawnChance":0,"BossMonsterCount":0,"BossMonster_MonsterVarietiesKeys":[],"WorldAreasKeys":[],"__col9":[]}]
                """);
            File.WriteAllText(Path.Combine(t, "MonsterPackEntries.json"), """
                [{"MonsterPacksKey":0,"MonsterVarietiesKey":10,"Weight":1},{"MonsterPacksKey":1,"MonsterVarietiesKey":11,"Weight":1}]
                """);
            File.WriteAllText(Path.Combine(t, "WorldAreas.json"), """
                [{"Id":"1_1_2","AreaLevel":2,"Bosses_MonsterVarietiesKeys":[]},{"Id":"MapAtlasBeach","AreaLevel":69,"Bosses_MonsterVarietiesKeys":[7]}]
                """);
            File.WriteAllText(Path.Combine(t, "Maps.json"), """
                [{"Regular_WorldAreasKey":1,"MonsterPacksKeys":[1]},{"Regular_WorldAreasKey":1,"MonsterPacksKeys":[1]}]
                """);
            var packs = new PackCatalog(dir);
            var beach = packs.For("MapAtlasBeach")!;
            Assert.Equal(["atlas-crab1"], beach.Scattered.Select(s => s.Pack.Id));
            Assert.Equal(PackCatalog.MapPackWeight, beach.Scattered[0].Weight);
            Assert.Equal(69, beach.AreaLevel);
            Assert.Equal(["Act1-CoastCrabs"], packs.For("1_1_2")!.Scattered.Select(s => s.Pack.Id));
        }
        finally { Directory.Delete(dir, true); }
    }
}
