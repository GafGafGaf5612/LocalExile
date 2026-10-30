using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    /// <summary>
    /// Applies a tattoo to an allocated passive. This is the server-side pilot path used before the client's
    /// tattoo-use packet is traced: the client command names the tattoo and the passive graph id. The tattoo is
    /// saved independently of the allocation, so refunding the passive keeps it.
    /// </summary>
    internal static async Task<string?> ApplyTattoo(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string name, ushort node, byte? consumeRow = null, uint consumeItemId = 0)
    {
        var tree = PassivesFor(conn);
        if (TattooCatalogue.Find(name) is not { } entry) return $"no tattoo \"{name}\"";
        if (tree.Find(node) is not { } passive) return $"unknown passive {node}";
        if (!conn.Passives.Contains(node)) return $"{passive.Name} ({node}) is not allocated";
        if (!TargetMatches(passive, entry.Target)) return $"{entry.Name} replaces {entry.Target}, not {passive.Name} ({node})";
        if (conn.Tattoos.GetValueOrDefault(node) == entry.Name) return $"{passive.Name} ({node}) already has {entry.Name}";
        if (TattooStats.For(entry.Name) is null) return $"{entry.Name} has no server effect yet";
        if (!conn.Tattoos.ContainsKey(node) && conn.Tattoos.Count >= 50) return "a character can have at most 50 tattoos";
        if (entry.EventOnly) return $"{entry.Name} is event-only and is not supported yet";
        if (entry.Name.StartsWith("Loyalty ", StringComparison.Ordinal)) return "Loyalty tattoos are not supported yet";
        if (entry.Name.StartsWith("Ancestral ", StringComparison.Ordinal)) return "Ancestral tattoos are not supported yet";
        if (entry.Name == "Journey Tattoo of Makanui") return "Journey Makanui is not supported yet";
        if (entry.Name == "Journey Tattoo of the Body" || entry.Name == "Journey Tattoo of the Mind"
            || entry.Name == "Journey Tattoo of the Soul") return "Journey tattoos are not supported yet";
        if (entry.Name.StartsWith("Honoured Tattoo of the ", StringComparison.Ordinal)
            && entry.Target.StartsWith("Notable", StringComparison.Ordinal)) return "Honoured attribute notables are not supported yet";

        var next = new Dictionary<ushort, string>(conn.Tattoos) { [node] = entry.Name };
        var items = ItemsOf(conn);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(conn), next);
                return consumed;
            });
            if (used is not { } consumed) return "the tattoo item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(conn), next);
        lock (conn.Monsters)
        {
            conn.Tattoos = next;
            conn.TattooRevision++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo used" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "tattoo");
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} tattoo {entry.Name} on {node} {passive.Name} ({conn.Tattoos.Count} applied)");
        return null;
    }

    /// <summary>The client's tattoo-on-passive packet (C→S 0x77): consume one tattoo from the inventory and install
    /// it on the named allocated passive. The packet carries the inventory row, the item id and the passive graph id.</summary>
    internal static async Task UseTattooOnPassive(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceItems.UseItemOnPassive use, string hex)
    {
        if (PlayerInventories.Row(use.InventoryId) is not byte row)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo use: unknown inventory {use.InventoryId}: {hex}");
            return;
        }
        var items = ItemsOf(conn);
        if (items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo use: no item {use.ItemId} in inventory {use.InventoryId}: {hex}");
            return;
        }
        if (ItemsFor(conn).ByHash(item.Item.TypeHash) is not { } type)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 unknown item type {use.ItemId}: {hex}");
            return;
        }
        // The same 0x77 packet is used for an Orb of Scouring on a tattooed passive: the item decides whether the
        // passive is tattooed or restored.
        if (type.Id == CurrencyEffects.OrbOfScouring)
        {
            string? removed = await RemoveTattoo(remote, stream, ciphers, conn, use.Node, row, use.ItemId);
            if (removed is not null)
                Console.WriteLine($"[gateway] {remote} <= 0x0077 scouring passive {use.Node} refused: {removed}: {hex}");
            return;
        }
        if (TattooCatalogue.Find(type.Name) is not { } entry)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0077 item {use.ItemId} is not a tattoo: {hex}");
            return;
        }
        string? error = await ApplyTattoo(remote, stream, ciphers, conn, entry.Name, use.Node, row, use.ItemId);
        if (error is not null)
            Console.WriteLine($"[gateway] {remote} <= 0x0077 tattoo {entry.Name} on {use.Node} refused: {error}: {hex}");
    }

    /// <summary>Removes the tattoo from a passive and restores the passive's own stats.</summary>
    internal static async Task<string?> RemoveTattoo(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort node, byte? consumeRow = null, uint consumeItemId = 0)
    {
        if (PassivesFor(conn).Find(node) is not { } passive) return $"unknown passive {node}";
        if (!conn.Tattoos.ContainsKey(node)) return $"{passive.Name} ({node}) has no tattoo";
        var next = new Dictionary<ushort, string>(conn.Tattoos);
        next.Remove(node);
        var items = ItemsOf(conn);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(conn), next);
                return consumed;
            });
            if (used is not { } consumed) return "the currency item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(conn), next);
        lock (conn.Monsters)
        {
            conn.Tattoos = next;
            conn.TattooRevision++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo removed" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(remote, stream, ciphers, conn, "tattoo removed");
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} tattoo removed from {node} {passive.Name} ({conn.Tattoos.Count} applied)");
        return null;
    }

    internal static bool TargetMatches(PassiveTree.Node node, string target)
    {
        bool Has(string stat, int value) => node.Stats.Any(s => Stats.Name(s.Key) == stat && s.Value == value);
        bool small = !node.IsNotable && !node.IsKeystone && !node.IsAscendancy && !node.IsStart
            && !node.IsJewelSocket && !node.IsMastery && !node.IsProxy && !node.IsExpansion;
        return target switch
        {
            "Small Strength" => small && Has("base_strength", 10),
            "Small Dexterity" => small && Has("base_dexterity", 10),
            "Small Intelligence" => small && Has("base_intelligence", 10),
            "Small Attribute" => small && (Has("base_strength", 10) || Has("base_dexterity", 10) || Has("base_intelligence", 10)),
            "Notable (+30 to Strength)" => node.IsNotable && Has("base_strength", 30),
            "Notable (+30 to Dexterity)" => node.IsNotable && Has("base_dexterity", 30),
            "Notable (+30 to Intelligence)" => node.IsNotable && Has("base_intelligence", 30),
            "Keystone" => node.IsKeystone,
            "Mastery" => node.IsMastery,
            "Ascendancy Notable" => node.IsAscendancy,
            _ => false,
        };
    }
}
