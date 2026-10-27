partial class Program
{
    internal static readonly RuthlessRulesFile RuthlessSettings = new(
        Environment.GetEnvironmentVariable("Game_RUTHLESS_RULES") ?? Path.Combine("config", "ruthless.json"), Console.WriteLine);
    static readonly Lazy<ItemCatalog> RuthlessItems = new(() => new("data", ruthless: true));
    static readonly Lazy<SkillCatalog> RuthlessSkills = new(() => new("data", ruthless: true));
    static readonly Lazy<PassiveTree> RuthlessPassives = new(() => new("data", ruthless: true));
    static readonly Lazy<ItemRolls> RuthlessRolls = new(() => new(RuthlessItems.Value));
    static readonly Lazy<UniqueCatalog> RuthlessUniques = new(() => new(RuthlessItems.Value));
    static readonly Lazy<EssenceCrafting> RuthlessEssences = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<HarvestCrafting> RuthlessHarvest = new(() => new(RuthlessItems.Value, RuthlessRolls.Value));
    static readonly Lazy<ItemDrops> RuthlessDrops = new(() => new(RuthlessItems.Value, RuthlessRolls.Value, ruthless: true));

    internal static ItemCatalog ItemsFor(ClientState conn) => conn.Ruthless ? RuthlessItems.Value : Items;
    internal static SkillCatalog SkillsFor(ClientState conn) => conn.Ruthless ? RuthlessSkills.Value : Skills;
    internal static PassiveTree PassivesFor(ClientState conn) => conn.Ruthless ? RuthlessPassives.Value : Passives;
    internal static ItemRolls RollsFor(ClientState conn) => conn.Ruthless ? RuthlessRolls.Value : Rolls;
    internal static UniqueCatalog UniquesFor(ClientState conn) => conn.Ruthless ? RuthlessUniques.Value : Uniques;
    internal static EssenceCrafting EssencesFor(ClientState conn) => conn.Ruthless ? RuthlessEssences.Value : Essences;
    internal static HarvestCrafting HarvestFor(ClientState conn) => conn.Ruthless ? RuthlessHarvest.Value : Harvest;
    internal static ItemDrops DropsFor(ClientState conn) => conn.Ruthless ? RuthlessDrops.Value : Drops;

    internal static void PersistRuthlessVitals(ClientState conn, bool bestEffort = false)
    {
        if (!conn.Ruthless) return;
        try
        {
            lock (conn.Monsters) CharacterSaves.SaveVitals(CharacterName(conn), conn.PlayerLife, conn.PlayerMana);
        }
        catch (Exception e) when (bestEffort)
        {
            Console.WriteLine($"[ruthless] {CharacterName(conn)}: resource pools not saved; will retry: {e.Message}");
        }
    }

    internal static (uint Life, uint Mana) InitialVitals(bool ruthless, CharacterSaves.Save? saved, uint maxLife, uint maxMana)
    {
        // New and legacy saves have no pools. A softcore death/relogin revives; HC death is refused before this point.
        bool alive = saved?.Life is > 0;
        return ruthless && alive ? (Math.Min(saved!.Life!.Value, maxLife), Math.Min(saved.Mana ?? maxMana, maxMana))
            : (maxLife, maxMana);
    }

    internal static IReadOnlyList<(uint Id, uint Life)> RecoverRuthlessBossesOnDeath(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!conn.Ruthless || conn.PlayerLife != 0 || conn.RuthlessDeathHandled) return [];
            conn.RuthlessDeathHandled = true;
            if (!conn.Layout.Id.StartsWith("1_", StringComparison.Ordinal)
                && !conn.Layout.Id.StartsWith("2_", StringComparison.Ordinal)) return [];
            var settings = RuthlessSettings.Current;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var at = conn.Walk.Position(now);
            List<(uint, uint)> changed = [];
            foreach (var (id, monster) in conn.Monsters)
                if (!monster.Dead && monster.Life > 0 && monster.Variety?.BossHealthBar == true
                    && Dist(monster.Walk.Position(now), at) <= ExperienceRange)
                {
                    uint life = RuthlessGameplay.BossLifeAfterDeath(monster.Life, monster.MaxLife, settings.CampaignBossLifeRecoveryFraction);
                    if (life == monster.Life) continue;
                    monster.Life = life;
                    changed.Add((id, life));
                }
            return changed;
        }
    }
}
