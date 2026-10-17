using System.Text.Json;
using GameServer.Data;

namespace GameServer.Data.Tests;

public class GameDataTests
{
    [Fact]
    public void Loads_tables_from_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Gameserver-gamedata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Stats.json"),
                """[{"Id":"life","IsLocal":false},{"Id":"mana","IsLocal":true}]""");

            using var data = GameData.Load(dir);

            Assert.Equal(1, data.TableCount);
            Assert.True(data.TryGetTable("stats", out var rows));
            Assert.Equal(2, rows.Count);
            Assert.Equal("life", rows[0].GetProperty("Id").GetString());
            Assert.True(rows[1].GetProperty("IsLocal").GetBoolean());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_table_throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Gameserver-gamedata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var data = GameData.Load(dir);
            Assert.Equal(0, data.TableCount);
            Assert.Throws<KeyNotFoundException>(() => data.Rows("Nope"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
