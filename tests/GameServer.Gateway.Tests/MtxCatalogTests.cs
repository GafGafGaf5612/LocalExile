namespace GameServer.Gateway.Tests;

/// <summary>
/// The owned microtransactions (Microtransactions.cs) over made-up tables: only MtxTypes rows the client can resolve in
/// MTXTypeGameSpecific are owned (a record it cannot resolve crashed the client, 23-microtransactions.md §1), ids are
/// the row + 1, and a saved pick becomes a worn-table entry.
/// </summary>
public class MtxCatalogTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "mtx-" + Guid.NewGuid().ToString("N"));

    public MtxCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        File.WriteAllText(Path.Combine(_folder, "tables", "MtxTypes.json"),
            """[{"HASH32": 100}, {"HASH32": 200}, {"HASH32": 300}, {"Name": "no hash"}]""");
        File.WriteAllText(Path.Combine(_folder, "tables", "MtxTypeGameSpecific.json"),
            """[{"HASH32": 300}, {"HASH32": 100}]""");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Only_rows_the_client_resolves_are_owned()
    {
        var catalog = new MtxCatalog(_folder);
        Assert.Equal([(100u, 1u), (300u, 3u)], catalog.Owned.Select(r => (r.MtxTypeHash, r.Id)));
        Assert.Null(catalog.Find(2));
        Assert.Equal(300u, catalog.Find(3)?.MtxTypeHash);
        Assert.Equal(1u, catalog.ByHash(100)?.Id);
    }

    [Fact]
    public void A_saved_pick_is_a_worn_entry_and_an_unknown_one_is_dropped()
    {
        var catalog = new MtxCatalog(_folder);
        var worn = catalog.Equipped(new Dictionary<byte, CharacterSaves.SavedCosmetic>
        {
            [20] = new(3, Option: 1),
            [9] = new(2),        // MtxTypes row 2 has no MtxTypeGameSpecific row: not sent
            [60] = new(1),       // past the client's 50 slots
        });
        var entry = Assert.Single(worn);
        Assert.Equal((byte)20, entry.Slot);
        Assert.Equal((3u, 300u, (byte)1), (entry.Id, entry.MtxTypeHash, entry.Option));
    }

    [Fact]
    public void Without_tables_nothing_is_owned()
    {
        Assert.Empty(new MtxCatalog(Path.Combine(_folder, "missing")).Owned);
    }
}
