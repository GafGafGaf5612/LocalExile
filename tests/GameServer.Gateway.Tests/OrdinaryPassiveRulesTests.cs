using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class OrdinaryPassiveRulesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "localexile-ordinary-passives-" + Guid.NewGuid().ToString("N"));

    public OrdinaryPassiveRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        Write("tables/Characters.json", new[] { new { IntegerId = 3 }, new { IntegerId = 0 }, new { IntegerId = 1 } });
        Write("tables/Stats.json", new[] { new { Id = "display_can_take_character_start_point" } });
        object Passive(int id, int[]? characters = null, bool ascendancy = false, int? grant = null) => new
        {
            PassiveSkillGraphId = id,
            Characters = characters ?? [],
            AscendancyKey = ascendancy ? (int?)0 : null,
            Stats = grant is null ? Array.Empty<int>() : [0],
            Stat1Value = grant ?? 0,
            MasteryGroup = id == 14 ? 0 : -1,
            IsProxyPassive = id == 97,
            IsExpansion = id == 98,
        };
        Write("tables/PassiveSkills.json", new[]
        {
            Passive(100, [2]), Passive(200, [0]), Passive(300, [1]),
            Passive(10), Passive(11), Passive(12), Passive(13), Passive(14),
            Passive(20), Passive(21), Passive(30), Passive(90), Passive(97), Passive(98), Passive(99),
            Passive(70, ascendancy: true), Passive(80, ascendancy: true, grant: 3),
        });
        object Graph(bool phrecia) => new
        {
            nodes = new[]
            {
                new { id = 100, adjacent = new[] { 10 } },
                new { id = 200, adjacent = new[] { 20 } },
                new { id = 300, adjacent = new[] { 30, 70 } },
                new { id = 10, adjacent = phrecia ? new[] { 12, 97, 200 } : new[] { 11, 12, 97, 200 } },
                new { id = 11, adjacent = new[] { 13 } },
                new { id = 12, adjacent = new[] { 13 } },
                new { id = 13, adjacent = new[] { 14 } },
                new { id = 14, adjacent = new[] { 21 } }, // one mastery shared by two notables.
                new { id = 20, adjacent = new[] { 21 } },
                new { id = 21, adjacent = Array.Empty<int>() },
                new { id = 30, adjacent = Array.Empty<int>() },
                new { id = 90, adjacent = Array.Empty<int>() },
                new { id = 97, adjacent = new[] { 98 } },
                new { id = 98, adjacent = Array.Empty<int>() },
                new { id = 70, adjacent = phrecia ? Array.Empty<int>() : new[] { 80 } },
            }.Concat(phrecia ? [] : new[] { new { id = 80, adjacent = Array.Empty<int>() } }).ToArray(),
        };
        Write(AscendancyCatalog.GraphFile, new { revision = AscendancyCatalog.Revision, normal = Graph(false), phrecia = Graph(true) });
    }

    public void Dispose()
    {
        string tables = Path.Combine(_folder, "tables");
        foreach (string file in Directory.EnumerateFiles(tables)) File.Delete(file);
        Directory.Delete(tables);
        foreach (string file in Directory.EnumerateFiles(_folder)) File.Delete(file);
        Directory.Delete(_folder);
    }
    OrdinaryPassiveRules Rules => new(_folder);
    void Write(string relativePath, object value) => File.WriteAllText(Path.Combine(_folder, relativePath), JsonSerializer.Serialize(value));

    [Fact]
    public void Entire_batch_is_checked_independently_of_request_order_and_edges_are_undirected()
    {
        Rules.Validate(1, AscendancyFamily.Normal, [14, 13, 11, 10]);
        Rules.Validate(3, AscendancyFamily.Normal, [21, 20]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [14, 13]));
    }

    static string? RealDataFolder() => File.Exists(Path.Combine("data", AscendancyCatalog.GraphFile)) &&
        File.Exists("data/tables/PassiveSkills.json") ? "data" : null;
}
