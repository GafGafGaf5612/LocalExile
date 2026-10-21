using GameServer.Core.Protocol.Instance;

/// <summary>
/// The Survivors league (Agents/docs/survivors.md): a game mode in the spirit of the "survivors" genre. Characters start
/// in their hideout with a Beach Map; a map opened by the device takes one player in once (one life per map), and in the
/// map the character only moves: its skills fire on their own, the monsters come in endless waves around it, and every
/// character level gained offers a choice of three upgrades (/1, /2, /3). After <see cref="RunSeconds"/> of survival the
/// map's boss comes; killing it wins the run. Levels, passives and loot stay with the character; the upgrades last one
/// run.
/// </summary>
static class Survivors
{
    /// <summary>The base league's name; its Hardcore / SSF / Ruthless forms count too (<see cref="IsLeague"/>).</summary>
    public const string League = "Survivors";

    public static bool IsLeague(string? league) => league is not null && league.Contains(League, StringComparison.OrdinalIgnoreCase);

    /// <summary>The start kit's map: the Beach Map that opens MapAtlasBeach (its walk map is captured), and 40 Scrolls of
    /// Wisdom (a full stack). The class's skill and support gems are left out: the run's skills are its own.</summary>
    public const string MapItem = "Metadata/Items/Maps/MapAtlasBeach", Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
    public const int WisdomCount = 40;

    /// <summary>The chat countdown on entering the map, then the run starts.</summary>
    public const int CountdownSeconds = 5;

    /// <summary>The waves last 15 minutes of survival; then the boss comes.</summary>
    public const double RunSeconds = 15 * 60;

    /// <summary>How often the run steps (spawns, skills, regeneration).</summary>
    public const int StepMs = 200;

    /// <summary>Where the waves come from: a ring around the player, out of sight but not far.</summary>
    public const int SpawnMinRadius = 32, SpawnMaxRadius = 46;

    /// <summary>A hunting monster left this far behind is taken away; the waves bring a new one near the player.</summary>
    public const int LeftBehind = 110;

    /// <summary>The waves' monster level: the character's, +1 every 90 seconds.</summary>
    public static byte MonsterLevel(int characterLevel, double seconds) =>
        (byte)Math.Clamp(characterLevel + (int)(Math.Max(0, seconds) / 90), 1, 84);

    /// <summary>How many monsters the waves keep alive: 8 at the start, 6 more every minute, at most 70.</summary>
    public static int Crowd(double seconds) => Math.Min(70, 8 + (int)(Math.Max(0, seconds) * 6 / 60));

    /// <summary>At most this many new monsters per step, so a wave comes in over a few seconds.</summary>
    public const int SpawnsPerStep = 2;

    /// <summary>A wave monster's rarity: magic from 5% up to 20%, rare from 0 up to 5% over the run.</summary>
    public static MonsterRarity RollRarity(double seconds, Random rng)
    {
        double t = Math.Clamp(seconds / RunSeconds, 0, 1);
        double roll = rng.NextDouble() * 100;
        if (roll < 5 * t) return MonsterRarity.Rare;
        return roll < 5 * t + 5 + 15 * t ? MonsterRarity.Magic : MonsterRarity.Normal;
    }

    /// <summary>The same-level normal kills a character level takes in a run: 3 at level 1, then one more each level (12
    /// at level 10, 52 at 50).</summary>
    public static double KillsPerLevel(int level) => 2 + level;

    /// <summary>
    /// The league's experience multiplier at a character level: the game's curve asks ever more same-level kills per level
    /// (26 at level 1, 140 at 10, 1 070 at 50, 11 800 at 90: ExperienceLevels.dat over DefaultMonsterStats), so the
    /// multiplier brings that down to <see cref="KillsPerLevel"/>. Never below 1. <paramref name="levelStarts"/> is the
    /// experience at the start of each level (index = level − 1), <paramref name="monsterExperience"/> a normal monster's
    /// base experience at the character's level.
    /// </summary>
    public static double ExperienceMultiplier(int level, IReadOnlyList<uint> levelStarts, int monsterExperience)
    {
        if (level < 1 || level >= levelStarts.Count || monsterExperience <= 0) return 1;
        double kills = (levelStarts[level] - (double)levelStarts[level - 1]) / monsterExperience;
        return Math.Max(1, kills / KillsPerLevel(level));
    }

    /// <summary>A rare gives 10 times a normal monster's experience in the league (the game's rarity multiplier for rares
    /// is 5); magic and unique keep theirs.</summary>
    public static double RarityExperience(MonsterRarity rarity) => rarity == MonsterRarity.Rare ? 10 : GameServer.Core.World.Progression.RarityMultiplier(rarity);

    /// <summary>Monsters drop a Beach Map 1 time in this many (rares and uniques: 10 times as often).</summary>
    public const int MapDropOneIn = 400;

    public static bool DropsMap(MonsterRarity rarity, Random rng) =>
        rng.Next(rarity is MonsterRarity.Rare or MonsterRarity.Unique ? MapDropOneIn / 10 : MapDropOneIn) == 0;

    /// <summary>A run kills hundreds of monsters: a normal one drops its loot 1 time in this many.</summary>
    public const int NormalLootOneIn = 4;

    /// <summary>A boss killed: two Beach Maps.</summary>
    public const int BossMaps = 2;

    /// <summary>An upgrade a pick can give: a skill (its first level grants it) or a passive.</summary>
    public enum Upgrade
    {
        Fireball, FlameRing, FireBurst, FireNova, Meteor,
        Might, Haste, Regeneration, Reach, Vampirism, Swiftness, Toughness, Wisdom, Precision, Multishot,
        Mending,
    }

    public sealed record UpgradeInfo(Upgrade Id, string Name, int MaxLevel, bool Skill, Func<int, string> Describe);

    public static readonly IReadOnlyList<UpgradeInfo> Upgrades =
    [
        new(Upgrade.Fireball, "Fireball", 6, true, l => l == 1 ? "fires at the nearest enemies"
            : $"{FireballProjectiles(l)} projectile(s), {FireballDamage(l):0.##}x damage"),
        new(Upgrade.FlameRing, "Flame Ring", 5, true, l => l == 1 ? "burns enemies around you"
            : $"radius {FlameRingRadius(l, 1)}, {FlameRingDamage(l):0.##}x burning"),
        new(Upgrade.FireBurst, "Fire Burst", 5, true, l => l == 1 ? "explodes among the enemies"
            : $"every {FireBurstSeconds(l, 1):0.#} s, {FireBurstDamage(l):0.##}x damage"),
        new(Upgrade.FireNova, "Fire Nova", 5, true, l => l == 1 ? "fireballs burst out all around you"
            : $"{FireNovaProjectiles(l)} fireballs every {FireNovaSeconds(l, 1):0.#} s"),
        new(Upgrade.Meteor, "Meteor", 5, true, l => l == 1 ? "crushes the toughest enemy near you"
            : $"every {MeteorSeconds(l, 1):0.#} s, {MeteorDamage(l):0.##}x damage"),
        new(Upgrade.Might, "Might", 5, false, l => $"{20 * l}% more skill damage"),
        new(Upgrade.Haste, "Haste", 5, false, l => $"{8 * l}% faster skills"),
        new(Upgrade.Regeneration, "Regeneration", 5, false, l => $"regenerate {l}% of life per second"),
        new(Upgrade.Reach, "Reach", 3, false, l => $"{15 * l}% more area and range"),
        new(Upgrade.Vampirism, "Vampirism", 3, false, l => $"recover {l}% of life per kill"),
        new(Upgrade.Swiftness, "Swiftness", 4, false, l => $"{MovementSpeedPerLevel * l}% more movement speed"),
        new(Upgrade.Toughness, "Toughness", 5, false, l => $"{ToughnessPerLevel * l}% less damage taken"),
        new(Upgrade.Wisdom, "Wisdom", 5, false, l => $"{15 * l}% more experience"),
        new(Upgrade.Precision, "Precision", 5, false, l => $"{8 * l}% chance to deal double damage"),
        new(Upgrade.Multishot, "Multishot", 2, false, l => $"+{l} projectile(s) for Fireball and Fire Nova"),
        // Never offered by its own: the choice when nothing else can be upgraded any more.
        new(Upgrade.Mending, "Mending", 0, false, _ => "recover 30% of life"),
    ];

    public static UpgradeInfo Info(Upgrade u) => Upgrades.First(i => i.Id == u);

    // The skills' numbers. Damage is a share of a normal monster's life at the character's level (SurvivorsDamage), so
    // the skills keep up with the character while the waves' level climbs past it.
    public static int FireballProjectiles(int level) => 1 + level / 2;
    public static double FireballDamage(int level) => 0.55 * (1 + 0.2 * (level - 1));
    public static double FireballSeconds(int level, double haste) => 1.3 / haste;
    public const int FireballRange = 70;
    public static int FlameRingRadius(int level, double reach) => (int)Math.Round((12 + 3 * level) * reach);
    public static double FlameRingDamage(int level) => 0.3 * (1 + 0.3 * (level - 1));
    public static double FireBurstSeconds(int level, double haste) => (3.2 - 0.3 * (level - 1)) / haste;
    public static double FireBurstDamage(int level) => 1.2 * (1 + 0.25 * (level - 1));
    public static int FireBurstRadius(int level, double reach) => (int)Math.Round((9 + level) * reach);
    public const int FireBurstRange = 55;
    public static int FireNovaProjectiles(int level) => 6 + 2 * level;
    public static double FireNovaSeconds(int level, double haste) => (4.5 - 0.4 * (level - 1)) / haste;
    public static double FireNovaDamage(int level) => 0.45 * (1 + 0.2 * (level - 1));
    /// <summary>How far the nova's fireballs are aimed (they fly on to their range).</summary>
    public const int FireNovaAim = 30;
    public static double MeteorSeconds(int level, double haste) => (5.0 - 0.5 * (level - 1)) / haste;
    public static double MeteorDamage(int level) => 4 * (1 + 0.35 * (level - 1));
    public static int MeteorRadius(int level, double reach) => (int)Math.Round(6 * reach);
    public const int MeteorRange = 60;
    public const int MovementSpeedPerLevel = 8, ToughnessPerLevel = 8;

    /// <summary>One run in a map: its phase and survival time, kills, the upgrades taken and the picks waiting.</summary>
    public sealed class Run(int characterLevel, Random rng)
    {
        public enum Phase { Countdown, Waves, Boss, Won, Lost }

        public Phase State = Phase.Countdown;
        /// <summary>Seconds survived: the run's own clock, which only moves while the area runs (not while it is paused
        /// or a pick is open) and the player lives.</summary>
        public double Elapsed;
        public int Kills, PendingPicks;
        /// <summary>The three upgrades offered, or null when no pick is open.</summary>
        public IReadOnlyList<Upgrade>? Offer;
        public readonly Dictionary<Upgrade, int> Levels = new() { [Upgrade.Fireball] = 1 };
        /// <summary>When each skill fires next (in <see cref="Elapsed"/> seconds).</summary>
        public readonly Dictionary<Upgrade, double> ReadyAt = [];
        /// <summary>The boss's object id once it is out.</summary>
        public uint Boss;
        /// <summary>The minutes announced so far.</summary>
        public int Minutes;
        /// <summary>The Flame Ring's effect object under the player, 0 before it is made.</summary>
        public uint Ring;
        /// <summary>Life regenerated but not yet whole (fractions of a point between steps).</summary>
        public double RegenCarry;
        public int CharacterLevel = characterLevel;
        /// <summary>The map's monsters with their weights (its packs' entries), and its boss (null: a rare of the pool).</summary>
        public readonly List<(MonsterCatalog.Variety Variety, int Weight)> Pool = [];
        public MonsterCatalog.Variety? BossVariety;

        public MonsterCatalog.Variety PickMonster()
        {
            int roll = rng.Next(Math.Max(1, Pool.Sum(p => p.Weight)));
            foreach (var (variety, weight) in Pool) if ((roll -= weight) < 0) return variety;
            return Pool[^1].Variety;
        }

        public bool Over => State is Phase.Won or Phase.Lost;

        public int Level(Upgrade u) => Levels.GetValueOrDefault(u);
        public double DamageMultiplier => 1 + 0.2 * Level(Upgrade.Might);
        public double Haste => 1 + 0.08 * Level(Upgrade.Haste);
        public double Reach => 1 + 0.15 * Level(Upgrade.Reach);
        public int MovementSpeedPct => MovementSpeedPerLevel * Level(Upgrade.Swiftness);
        /// <summary>What is left of a hit on the player after Toughness.</summary>
        public double DamageTaken => 1 - ToughnessPerLevel * Level(Upgrade.Toughness) / 100.0;
        public double ExperienceMultiplier => 1 + 0.15 * Level(Upgrade.Wisdom);
        public int DoubleDamageChance => 8 * Level(Upgrade.Precision);
        public int ExtraProjectiles => Level(Upgrade.Multishot);

        /// <summary>Character levels gained in the run: one pick each.</summary>
        public void LevelledUp(int newLevel, int levels)
        {
            CharacterLevel = newLevel;
            if (!Over && levels > 0) PendingPicks += levels;
        }

        /// <summary>Three different upgrades that can still go up (fewer when fewer can), or Mending when none can.</summary>
        public IReadOnlyList<Upgrade> RollOffer()
        {
            var open = Upgrades.Where(u => u.MaxLevel > Level(u.Id)).Select(u => u.Id).OrderBy(_ => rng.Next()).Take(3).ToList();
            return open.Count == 0 ? [Upgrade.Mending] : open;
        }

        /// <summary>Opens the next pick if one waits and none is open. Returns the offer, or null.</summary>
        public IReadOnlyList<Upgrade>? OpenPick()
        {
            if (Offer is not null || PendingPicks == 0 || Over) return null;
            PendingPicks--;
            return Offer = RollOffer();
        }

        /// <summary>Takes choice <paramref name="choice"/> (1-based) of the open pick. Returns the upgrade, or null when
        /// no pick is open or the number is not one of its choices.</summary>
        public Upgrade? Choose(int choice)
        {
            if (Offer is not { } offer || choice < 1 || choice > offer.Count) return null;
            var u = offer[choice - 1];
            Offer = null;
            if (u != Upgrade.Mending) Levels[u] = Level(u) + 1;
            return u;
        }

        /// <summary>A skill hit's damage roll after Precision: doubled at its chance.</summary>
        public int Precise(int damage) => rng.Next(100) < DoubleDamageChance ? damage * 2 : damage;

        /// <summary>The upgrades taken, for the chat: "Fireball 2, Might 1".</summary>
        public string Describe() => string.Join(", ", Levels.Where(kv => kv.Value > 0).Select(kv => $"{Info(kv.Key).Name} {kv.Value}"));
    }

    /// <summary>The chat line of one choice: "1. Fireball (level 2) - 2 projectile(s), 0.66x damage".</summary>
    public static string ChoiceLine(int number, Upgrade u, Run run)
    {
        var info = Info(u);
        int next = run.Level(u) + 1;
        return u == Upgrade.Mending ? $"{number}. {info.Name} - {info.Describe(0)}"
            : $"{number}. {info.Name} ({(next == 1 ? "new" : $"level {next}")}) - {info.Describe(next)}";
    }

    /// <summary>"mm:ss".</summary>
    public static string Time(double seconds) => $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";
}
