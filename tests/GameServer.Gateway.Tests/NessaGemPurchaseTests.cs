using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class NessaGemPurchaseTests : IDisposable
{
    const string Nessa = "Metadata/NPC/Act1/Nessa";
    readonly List<string> characters = [];

    (ClientState Conn, PlayerInventories Inventory, InventoryItem Template) Shop(Item gem, int coins = 3,
        string npc = Nessa, uint tab = NpcShops.FirstTabId)
    {
        TestSaves.Ensure();
        string name = "GemBuyer-" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        CharacterSaves.Write(name, new([]));
        var inventory = PlayerInventories.For(name, Program.Items);
        var template = new InventoryItem(inventory.NewItemId(), 4, 2, gem);
        if (coins > 0)
        {
            var (currency, amount) = gem.Price[0];
            inventory.Add(0, 11, 4, ItemCatalog.Create(Program.Items.ByHash(currency)!) with { StackSize = (ushort)(coins * amount) });
        }
        var state = new ClientState(new AreaLayout(1, "shop-test", 200, 200, 10, 10, null, []), 1, 1) { Character = name };
        state.Shop = new(new(new NpcCatalog.Npc(0, npc, "Vendor", [], null, 0), 0, 0),
            new() { [template.Id] = (tab, template) });
        return (state, inventory, template);
    }

    static Task Buy(ClientState conn, uint id, byte x, NetworkStream stream, ConnectionCiphers ciphers, bool control = false) =>
        control ? Program.CtrlBuyFromShop("shop-test", stream, ciphers, conn, new(id, 0, 0, 0, 0))
            : Program.BuyFromShop("shop-test", stream, ciphers, conn, new(id, 1, x, 0, 0, 0, 0));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_gem_tab_can_sell_the_same_template_twice_with_separate_paid_persistent_items(bool control)
    {
        uint next = 1;
        var catalog = Program.Items;
        var fireball = catalog.FindGem("Fireball")!.Value.Type;
        var tabs = NpcShops.Tabs(Nessa, catalog, () => next++, [], gemStock: [fireball.Row], fullGemStock: true);
        Assert.Contains(tabs, t => t.Name == "Skills");
        Assert.Contains(tabs, t => t.Name == "Goods");
        for (int index = 0; index < tabs.Count; index++)
        {
            var sample = tabs[index].Items.Where(i => i.Item.Components.Contains("SkillGem"))
                .OrderByDescending(i => i.Item.GemVariant).FirstOrDefault();
            if (sample is null) continue;
            uint tab = NpcShops.FirstTabId + (uint)index;
            var (conn, inventory, template) = Shop(sample.Item with { Quality = 20 }, tab: tab);
            var (currency, amount) = template.Item.Price[0];
            byte[] bytes = await Exchange(async (stream, ciphers) =>
            {
                await Buy(conn, template.Id, 0, stream, ciphers, control);
                await Buy(conn, template.Id, 1, stream, ciphers, control);
            });
            Assert.Equal(template, conn.Shop!.Items[template.Id].Item);
            Assert.Equal((int)amount, inventory.Count(0, currency));
            var bought = inventory.Items(0).Where(i => i.Item.Components.Contains("SkillGem")).ToList();
            Assert.Equal(2, bought.Count);
            Assert.Equal(2, bought.Select(i => i.Id).Distinct().Count());
            Assert.All(bought, i =>
            {
                Assert.NotEqual(template.Id, i.Id);
                Assert.Equal(template.Item.GemVariant, i.Item.GemVariant);
                Assert.Equal(20, i.Item.Quality);
                Assert.Empty(i.Item.Price);
            });
            for (uint revision = 1; revision <= 2; revision++)
                Assert.True(bytes.AsSpan().IndexOf(InstanceItems.InventoryChange(tab, revision,
                    [template.Id], [template], NpcShops.Manager)) >= 0, tabs[index].Name);
            PlayerInventories.Forget(conn.Character!);
            var reloaded = PlayerInventories.For(conn.Character!, catalog).Items(0);
            Assert.Equal(bought.Select(i => (i.Id, i.Item.GemVariant, i.Item.Quality)).Order(),
                reloaded.Where(i => i.Item.Components.Contains("SkillGem"))
                    .Select(i => (i.Id, i.Item.GemVariant, i.Item.Quality)).Order());
        }
    }

    Item PricedGem()
    {
        var gem = Program.Items.FindGem("Ice Nova of Frostbolts")!.Value;
        return ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index,
            Price = [(Program.Items.Find("Scroll of Wisdom")!.Hash, 1)] };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insufficient_currency_leaves_the_template_and_inventory_unchanged(bool control)
    {
        var (conn, inventory, template) = Shop(PricedGem(), coins: 0);
        var bytes = await Exchange((stream, ciphers) => Buy(conn, template.Id, 0, stream, ciphers, control));
        Assert.Empty(bytes);
        Assert.Empty(inventory.Items(0));
        Assert.Equal(template, conn.Shop!.Items[template.Id].Item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_save_keeps_the_template_and_does_not_charge_or_deliver_a_gem(bool control)
    {
        var (conn, _, template) = Shop(PricedGem());
        Saves.FailWrite = key => key == conn.Character;
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => Exchange((stream, ciphers) => Buy(conn, template.Id, 0, stream, ciphers, control)));
        }
        finally { Saves.FailWrite = null; }
        PlayerInventories.Forget(conn.Character!);
        var inventory = PlayerInventories.For(conn.Character!, Program.Items);
        Assert.Equal(3, inventory.Count(0, template.Item.Price[0].TypeHash));
        Assert.DoesNotContain(inventory.Items(0), i => i.Item.Components.Contains("SkillGem"));
        Assert.Equal(template, conn.Shop!.Items[template.Id].Item);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Other_vendors_gems_and_nessas_non_gem_goods_remain_finite(bool otherVendor, bool control)
    {
        var item = otherVendor ? PricedGem() : ItemCatalog.Create(Program.Items.Find("Small Life Flask")!) with
            { Price = [(Program.Items.Find("Scroll of Wisdom")!.Hash, 1)] };
        var (conn, inventory, template) = Shop(item, npc: otherVendor ? "Metadata/NPC/Act2/Yeena" : Nessa);
        await Exchange(async (stream, ciphers) =>
        {
            await Buy(conn, template.Id, 0, stream, ciphers, control);
            await Buy(conn, template.Id, 1, stream, ciphers, control);
        });
        Assert.Empty(conn.Shop!.Items);
        Assert.Equal(2, inventory.Count(0, template.Item.Price[0].TypeHash));
        var bought = Assert.Single(inventory.Items(0), i => i.Item.TypeHash == template.Item.TypeHash);
        if (!control) Assert.Equal(template.Id, bought.Id);
    }

    [Fact]
    public async Task Ctrl_purchase_with_a_full_inventory_keeps_the_offer_without_charging()
    {
        var (conn, inventory, template) = Shop(PricedGem());
        var scroll = Program.Items.Find("Scroll of Wisdom")!;
        for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 12; x++)
                if (x != 11 || y != 4) inventory.Add(0, x, y, ItemCatalog.Create(scroll) with { StackSize = 40 });
        int before = inventory.Count(0, scroll.Hash);
        var bytes = await Exchange((stream, ciphers) => Buy(conn, template.Id, 0, stream, ciphers, control: true));
        Assert.Empty(bytes);
        Assert.Equal(before, inventory.Count(0, scroll.Hash));
        Assert.DoesNotContain(inventory.Items(0), i => i.Item.Components.Contains("SkillGem"));
        Assert.Equal(template, conn.Shop!.Items[template.Id].Item);
    }

    [Fact]
    public async Task Opening_nessas_shop_sends_inventories_and_dialogue_without_chat_explanations()
    {
        var (conn, _, _) = Shop(PricedGem(), coins: 0);
        var catalog = NpcCatalog.Load();
        var npc = new SpawnedNpc(catalog.Find(Nessa)!, 0, 0);
        const uint npcObject = 0x1234;
        var bytes = await Exchange((stream, ciphers) => Program.OpenNpcShop("shop-test", stream, ciphers, conn, npcObject, npc));
        uint next = 1;
        var names = NpcShops.Tabs(Nessa, Program.Items, () => next++, [], gemStock: [], fullGemStock: true)
            .Select(t => t.Name).ToArray();
        var ids = names.Select((_, i) => NpcShops.FirstTabId + (uint)i).ToArray();
        var ending = InstanceNpcs.Dialogue(npcObject, (ushort)Math.Max(0, catalog.TalkSource(npc.Npc).ShopRow),
            true, ids, names.Select((name, i) => new InstanceNpcs.Choice(i, name)).ToArray());
        int inventoriesLength = ids.Sum(id => InstanceInventories.AddInventory(id, NpcShops.Tab, NpcShops.Manager,
            conn.Shop!.Items.Values.Where(v => v.Tab == id).Select(v => v.Item).ToArray()).Length);
        Assert.Equal(inventoriesLength + ending.Length, bytes.Length);
        Assert.Equal(ending, bytes[^ending.Length..]);
    }

    static async Task<byte[]> Exchange(Func<NetworkStream, ConnectionCiphers, Task> work)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accept;
        using var capture = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
        byte[] key = new byte[64];
        try { await work(server.GetStream(), new(key, isClient: false)).WaitAsync(timeout.Token); }
        finally { server.Client.Shutdown(SocketShutdown.Send); await reading; }
        var bytes = capture.ToArray();
        new ConnectionCiphers(key, isClient: true).Receive.Process(bytes);
        return bytes;
    }

    public void Dispose()
    {
        Saves.FailWrite = null;
        foreach (var name in characters)
        {
            PlayerInventories.Forget(name);
            AccountStash.Forget(AccountStash.Key(name, null));
        }
    }
}
