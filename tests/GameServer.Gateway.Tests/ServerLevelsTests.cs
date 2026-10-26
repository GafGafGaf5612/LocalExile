namespace GameServer.Gateway.Tests;

// Which areas the Gateway generates itself: those whose seed-1 trace passes every gate check and whose logged loads
// all match (scripts/re/levelgen/gate-baseline.json).
public class ServerLevelsTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"baseline{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_file);

    [Fact] public void OnlyAreasPassingEveryCheckAreSupported()
    {
        File.WriteAllText(_file, """
        {
          "capture good": {"Hash":"match","Walk":0,"Projectile":0,"Placed":3,"Matched":3,"Captured":3,"Heights":0,"Objects":0},
          "capture stopped": {"Hash":"match","FirstDiff":"0x141530658 (port stopped)","Walk":0,"Projectile":0,"Captured":-1},
          "capture drawdiff": {"Hash":"match","FirstDiff":"0x1420437eb","Walk":0,"Projectile":0},
          "capture grid": {"Hash":"match","Walk":4,"Projectile":0},
          "capture doodads": {"Hash":"match","Walk":0,"Projectile":0,"Placed":3,"Matched":2,"Captured":3},
          "capture heights": {"Hash":"match","Walk":0,"Projectile":0,"Heights":12},
          "capture objects": {"Hash":"match","Walk":0,"Projectile":0,"Objects":1},
          "capture hash": {"Hash":"mismatch","Walk":0,"Projectile":0},
          "capture oracle": {"Hash":"match","Walk":0,"Projectile":0},
          "oracle oracle 77": {"Hash":"mismatch"},
          "oracle good 5": {"Hash":"match"}
        }
        """);
        Assert.Equal(["good", "stopped"], ServerLevels.LoadSupported(_file).Order());
    }

    [Fact] public void NoBaselineSupportsNothing() => Assert.Empty(ServerLevels.LoadSupported(_file + ".missing"));
}
