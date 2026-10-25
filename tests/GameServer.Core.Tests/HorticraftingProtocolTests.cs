using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class HorticraftingProtocolTests
{
    [Fact]
    public void Harvest_opens_its_own_native_window_without_a_bench_type_or_object_parameter()
    {
        Assert.Equal("007B3900000000000000000000000000000000",
            Convert.ToHexString(InstanceWaypoints.OpenUi(InstanceWaypoints.HorticraftingUi)));
        Assert.NotEqual(InstanceWaypoints.CraftingBenchUi, InstanceWaypoints.HorticraftingUi);
    }

    [Fact]
    public void Standard_station_serializes_only_its_five_server_components()
    {
        var station = new HideoutObject(100, 120, 0,
            ["Positioned", "BaseEvents", "Animated", "MinimapIcon", "InteractionAction"], new HideoutDoodad(0, 0));
        Assert.Equal(24, station.Blob().Length);
    }
}
