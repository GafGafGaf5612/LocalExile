using System.Text.Json;
using GameServer.Core.World;

/// <summary>
/// A character's level-based pools and attribute bonuses, from its class, level and allocated passives: what the server
/// sends as final stats (the client derives none, 11-skills.md §4). Path of Exile's base rules, before items and buffs,
/// with the per-level values of Metadata/Characters/Character.ot (life_per_level 12, mana_per_level 6,
/// accuracy_rating_per_level 2, base_evasion_rating 15 and no evasion per level):
/// life = (Characters.dat BaseMaxLife (38) + 12 per level + 1 per 2 strength + flat life) × (1 + increased life);
/// mana likewise with BaseMaxMana (34), 6 per level and intelligence; accuracy = (2 per level + 2 per dexterity + flat)
/// × (1 + increased accuracy / 100), with the Accuracy Mastery's per-level and per-dexterity replacements;
/// armour = flat armour × (1 + increased armour / 100); evasion = (15 + flat evasion) raised by 1% per 5 dexterity and
/// increased evasion; 1% increased maximum energy shield per 5 intelligence and 1% increased melee physical damage per 5
/// strength. Without these stats the client falls back to the base for level 1 (the Witch's 50 life and 40 mana).
/// </summary>
static class CharacterStats
{
    public sealed record Pools(int Level, uint Life, uint Mana, int Accuracy, uint Experience,
        int Armour = 0, int ArmourPct = 0, int Evasion = BaseEvasion, int EvasionPct = 0,
        int EnergyShieldPct = 0, int MeleePhysicalDamagePct = 0,
        int Strength = 0, int Dexterity = 0, int Intelligence = 0,
        int FireResistance = BaseResistance, int ColdResistance = BaseResistance,
        int LightningResistance = BaseResistance, int ChaosResistance = BaseResistance,
        int FireMaxResistance = DamageModel.DefaultMaxResistance, int ColdMaxResistance = DamageModel.DefaultMaxResistance,
        int LightningMaxResistance = DamageModel.DefaultMaxResistance, int ChaosMaxResistance = DamageModel.DefaultMaxResistance,
        int SpellSuppressionChance = 0, int SpellSuppressionEffect = DamageModel.SpellSuppressionEffect,
        bool SpellSuppressionLucky = false, bool SpellSuppressionUnlucky = false,
        int SpellSuppressionWhileFullLife = 0, int SpellSuppressionWhileMoving = 0, int SpellSuppressionIfHitRecently = 0,
        int MovementVelocityPct = 0, int BlockChance = 0, int SpellBlockChance = 0,
        int BlockChanceMax = DamageModel.DefaultMaxBlockChance, int SpellBlockChanceMax = DamageModel.DefaultMaxSpellBlockChance,
        int BlockChanceBase = 0, int BlockChanceUncapped = 0,
        int SpellBlockPer5AttackBlock = 0, int LifeGainedOnBlock = 0, int ManaGainedOnBlock = 0,
        bool VersatileCombatant = false, bool AttackBlockLucky = false, bool SpellBlockLucky = false,
        int AttackSpeedPct = 0,
        long CriticalStrikeChancePct = 0, long SpellCriticalStrikeChancePct = 0,
        int CriticalStrikeMultiplier = DamageModel.BaseCriticalStrikeMultiplier,
        int SpellCriticalStrikeMultiplier = DamageModel.BaseCriticalStrikeMultiplier,
        int AttackCriticalStrikeChance = 0,
        int SpellCriticalStrikeChance = DamageModel.BaseSpellCriticalStrikeChancePerMyriad,
        int AccuracyPct = 0,
        // The Accuracy Mastery's target-conditional "more" effects, kept out of Accuracy: they only apply to a hit's
        // own target (TargetAccuracy), so the panel must not show them as the character's accuracy.
        int AccuracyMoreVsUniquePct = 0, int AccuracyMoreInCloseRangePct = 0,
        int ManaReservationEfficiencyPct = 0, int LifeReservationEfficiencyPct = 0,
        int AuraEffectPct = 0, int AuraAreaPct = 0, uint EnergyShield = 0,
        int ColdOvercapEvasionPct = 0, int LightningOvercapCritPct = 0)
    {
        // Identifies the transient source snapshot used by this immutable pool calculation, including with-clones.
        internal long FortificationRevision { get; init; }
    }

    /// <summary>What allocated passives and equipped base items add to the pools: attributes, flat and increased life,
    /// mana, armour and evasion, increased energy shield and melee physical damage, the base resistances, attack speed's
    /// increased and more multipliers, critical strike chance and multiplier, and the Accuracy Mastery's conversions.</summary>
    public sealed record Bonus(int Strength = 0, int Dexterity = 0, int Intelligence = 0, int Life = 0, int LifePct = 0,
        int Mana = 0, int ManaPct = 0, int Armour = 0, int ArmourPct = 0, int Evasion = 0, int EvasionPct = 0,
        int SharedDefencesPct = 0, bool IronReflexes = false,
        int EnergyShieldPct = 0, int MeleePhysicalDamagePct = 0,
        int FireResistance = 0, int ColdResistance = 0, int LightningResistance = 0, int ChaosResistance = 0,
        int FireMaxResistance = 0, int ColdMaxResistance = 0, int LightningMaxResistance = 0, int ChaosMaxResistance = 0,
        int SpellSuppressionChance = 0, int SpellSuppressionEffect = 0,
        bool SpellSuppressionLucky = false, bool SpellSuppressionUnlucky = false,
        int SpellSuppressionWhileFullLife = 0, int SpellSuppressionWhileMoving = 0, int SpellSuppressionIfHitRecently = 0,
        int MovementVelocityPct = 0, int BlockChance = 0, int SpellBlockChance = 0,
        int BlockChanceMax = 0, int SpellBlockChanceMax = 0,
        int SpellBlockPer5AttackBlock = 0, int LifeGainedOnBlock = 0, int ManaGainedOnBlock = 0,
        int BlockChanceInc = 0, int BlockChanceMore = 0,
        bool VersatileCombatant = false, bool AttackBlockLucky = false, bool SpellBlockLucky = false,
        int AttackSpeedPct = 0, int AttackSpeedMorePct = 0,
        long CriticalStrikeChancePct = 0, long SpellCriticalStrikeChancePct = 0,
        int CriticalStrikeMultiplier = 0, int SpellCriticalStrikeMultiplier = 0,
        int Accuracy = 0, int AccuracyPct = 0, int AccuracyPerLevel = 0,
        bool DexterityAccuracyTriple = false, bool AccuracyEqualsIntelligence = false,
        int AccuracyMoreVsUniquePct = 0, int AccuracyMoreInCloseRangePct = 0,
        int ManaReservationEfficiencyPct = 0, int LifeReservationEfficiencyPct = 0,
        int AuraEffectPct = 0, int AuraAreaPct = 0, decimal ArmourMorePct = 0, decimal EvasionMorePct = 0, int EnergyShield = 0,
        // The overcapped-resistance flags' derived increases (ResistanceOvercap), computed once in PoolsFor from the
        // final raw resistances and maxima: +1% per 1% of cold overcap on evasion, lightning overcap on crit chance.
        int ColdOvercapEvasionPct = 0, int LightningOvercapCritPct = 0,
        int ManaPerFourStrength = 0,
        // These local shield values are already included in Armour/Evasion/EnergyShield. Keep their identity so
        // shield-only increases can add to the global increase bucket without scaling other flat sources.
        int ShieldArmour = 0, int ShieldEvasion = 0, int ShieldEnergyShield = 0,
        int ShieldArmourPct = 0, int ShieldEvasionPct = 0, int ShieldEnergyShieldPct = 0,
        int SharedShieldDefencesPct = 0, bool NoShieldDefences = false)
    {
        public static readonly Bonus None = new();
    }

    /// <summary>Character.ot base_evasion_rating.</summary>
    public const int BaseEvasion = 15;

    /// <summary>Path of Exile's resistance before the quest penalties: 0% to all four types at character level 1.
    /// Act 5 and Act 10 Kitava each take another 30% away; see <see cref="ResistancePenalty"/> in the Gateway.</summary>
    public const int BaseResistance = 0;

    /// <summary>Strength, dexterity and intelligence from attribute stats: the single ones, all attributes and the
    /// pairs, each in its base_ and additional_ form.</summary>
    public static (int Strength, int Dexterity, int Intelligence) Attributes(Func<string, int> get)
    {
        int all = get("additional_all_attributes") + get("base_all_attributes");
        int strDex = get("base_strength_and_dexterity") + get("additional_strength_and_dexterity");
        int strInt = get("base_strength_and_intelligence") + get("additional_strength_and_intelligence");
        int dexInt = get("base_dexterity_and_intelligence") + get("additional_dexterity_and_intelligence");
        return (get("base_strength") + get("additional_strength") + all + strDex + strInt,
            get("base_dexterity") + get("additional_dexterity") + all + strDex + dexInt,
            get("base_intelligence") + get("additional_intelligence") + all + strInt + dexInt);
    }

    /// <summary>Spell Suppression chance with the conditional passives whose condition holds now (on full life, moving,
    /// hit an enemy recently), capped at 100%. Energy shield is not modelled, so its full-ES stat is not counted.</summary>
    public static int SuppressionChance(Pools p, bool fullLife, bool moving, bool hitRecently) =>
        Math.Min(DamageModel.SpellSuppressionChanceCap, p.SpellSuppressionChance
            + (fullLife ? p.SpellSuppressionWhileFullLife : 0)
            + (moving ? p.SpellSuppressionWhileMoving : 0)
            + (hitRecently ? p.SpellSuppressionIfHitRecently : 0));

    public static Pools For(SkillCatalog.ClassInfo c, int level, Bonus? bonus = null, string folder = "data")
    {
        var b = bonus ?? Bonus.None;
        level = Math.Clamp(level, 1, Progression.MaxLevel);
        int str = c.Strength + b.Strength, dex = c.Dexterity + b.Dexterity, intel = c.Intelligence + b.Intelligence;
        // Equipment ES-to-Life conversion can supply a large flat value. Keep the signed panel stat representable.
        uint life = (uint)Math.Clamp(decimal.Floor((c.BaseMaxLife + 12m * level + str / 2 + b.Life)
            * (100m + b.LifePct) / 100m), 1m, int.MaxValue);
        uint mana = b.ManaPerFourStrength == 0
            ? (uint)Math.Max(0, (c.BaseMaxMana + 6 * level + intel / 2 + b.Mana) * (100 + b.ManaPct) / 100)
            : StrengthMana.Maximum(c.BaseMaxMana + 6L * level + intel / 2 + b.Mana,
                str, b.ManaPerFourStrength, b.ManaPct);
        // PoB CalcOffence: accuracy = floor(base * (1 + increased / 100)) with no more multiplier modelled. The
        // Accuracy Mastery can replace the 2 per level and 2 per dexterity, and add Accuracy equal to Intelligence.
        int accuracyPerLevel = 2 + b.AccuracyPerLevel;
        int accuracyPerDex = b.DexterityAccuracyTriple ? 3 : 2;
        int accuracyBase = Math.Max(0, accuracyPerLevel * level + accuracyPerDex * dex + b.Accuracy
            + (b.AccuracyEqualsIntelligence ? intel : 0));
        int accuracy = Math.Max(0, (int)Math.Floor(accuracyBase * (1 + b.AccuracyPct / 100.0)));
        // PoB CalcDefence: armour and evasion are flat defences raised by their increased modifiers. Evasion keeps the
        // character's 15 base and gains 1% per 5 dexterity; armour has no base outside items and passives. The
        // overcapped cold resistance's increase is one of the evasion increases (it applies before the conversion, so
        // Iron Reflexes' converted evasion keeps it, as PoB's converted branch reads the same Evasion INC bucket).
        int armourPct = b.ArmourPct;
        // Native slot calculations add shield increases to the applicable global bucket. Sum fractional
        // contributions before rounding the final rating (AR 0x142732ee7, EV 0x14272a59b, ES 0x14252c6e7).
        decimal Increased(decimal total, int shield, long increase, long shieldIncrease)
        {
            decimal fromShield = Math.Clamp(shield, 0m, Math.Max(0m, total));
            return Math.Max(0m, total - fromShield) * Math.Max(0m, 100m + increase) / 100m
                + (b.NoShieldDefences ? 0m : fromShield * Math.Max(0m, 100m + increase + shieldIncrease) / 100m);
        }
        decimal More(decimal value, decimal increase)
        {
            decimal factor = Math.Max(0m, 100m + increase) / 100m;
            return factor > 1m && value > decimal.MaxValue / factor ? decimal.MaxValue : value * factor;
        }
        int Rating(decimal value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0m, int.MaxValue);
        decimal armourValue = More(Increased(b.Armour, b.ShieldArmour, armourPct,
            (long)b.ShieldArmourPct + b.SharedShieldDefencesPct), b.ArmourMorePct);
        int evasionPct = dex / 5 + b.EvasionPct + b.ColdOvercapEvasionPct;
        long evasionBase = BaseEvasion + (long)b.Evasion;
        decimal evasionValue = More(Increased(evasionBase, b.ShieldEvasion, evasionPct,
            (long)b.ShieldEvasionPct + b.SharedShieldDefencesPct), b.EvasionMorePct);
        if (b.IronReflexes)
        {
            // Iron Reflexes converts all evasion rating to armour and removes dexterity's evasion bonus. The converted
            // evasion keeps the armour, evasion and shared defence increases (each once), but not dexterity.
            long convertedPct = (long)armourPct + b.EvasionPct + b.ColdOvercapEvasionPct - b.SharedDefencesPct;
            decimal converted = More(More(Increased(evasionBase, b.ShieldEvasion, convertedPct,
                (long)b.ShieldArmourPct + b.ShieldEvasionPct + b.SharedShieldDefencesPct), b.EvasionMorePct), b.ArmourMorePct);
            armourValue = armourValue > decimal.MaxValue - converted ? decimal.MaxValue : armourValue + converted;
            evasionPct = b.EvasionPct + b.ColdOvercapEvasionPct;
            evasionValue = 0;
        }
        int armour = Rating(armourValue), evasion = Rating(evasionValue);
        int energyShieldPct = intel / 5 + b.EnergyShieldPct;
        uint energyShield = (uint)Rating(Increased(b.EnergyShield, b.ShieldEnergyShield, energyShieldPct,
            (long)b.ShieldEnergyShieldPct + b.SharedShieldDefencesPct));
        int Max(int bonus) => Math.Min(DamageModel.MaxResistCap, DamageModel.DefaultMaxResistance + bonus);
        int maxBlock = Math.Min(DamageModel.BlockChanceCap, DamageModel.DefaultMaxBlockChance + b.BlockChanceMax);
        int maxSpellBlock = Math.Min(DamageModel.BlockChanceCap, DamageModel.DefaultMaxSpellBlockChance + b.SpellBlockChanceMax);
        // Versatile Combatant: -10% to both maxima. The overcap spell block is applied after the weapon/shield base
        // in PoolsFor (the keystone reads the final uncapped attack chance).
        if (b.VersatileCombatant)
        {
            maxBlock = Math.Max(0, maxBlock - 10);
            maxSpellBlock = Math.Max(0, maxSpellBlock - 10);
        }
        // PoB CalcOffence: rate = base rate * (1 + increased / 100) * more. The client takes one integer
        // main_hand_attack_speed_+%, so send the equivalent increased value (93 for 75% increased and 10% more).
        int attackSpeedPct = (int)Math.Round(
            ((1 + b.AttackSpeedPct / 100.0) * (1 + b.AttackSpeedMorePct / 100.0) - 1) * 100,
            MidpointRounding.AwayFromZero);
        attackSpeedPct = Math.Max(-99, attackSpeedPct);
        return new(level, life, mana, accuracy, ExperienceFor(level, folder),
            Armour: armour, ArmourPct: armourPct, Evasion: evasion, EvasionPct: evasionPct,
            EnergyShieldPct: energyShieldPct, EnergyShield: energyShield,
            MeleePhysicalDamagePct: str / 5 + b.MeleePhysicalDamagePct,
            Strength: str, Dexterity: dex, Intelligence: intel,
            FireResistance: BaseResistance + b.FireResistance, ColdResistance: BaseResistance + b.ColdResistance,
            LightningResistance: BaseResistance + b.LightningResistance, ChaosResistance: BaseResistance + b.ChaosResistance,
            FireMaxResistance: Max(b.FireMaxResistance), ColdMaxResistance: Max(b.ColdMaxResistance),
            LightningMaxResistance: Max(b.LightningMaxResistance), ChaosMaxResistance: Max(b.ChaosMaxResistance),
            SpellSuppressionChance: Math.Min(DamageModel.SpellSuppressionChanceCap, b.SpellSuppressionChance),
            SpellSuppressionEffect: Math.Max(0, DamageModel.SpellSuppressionEffect + b.SpellSuppressionEffect),
            SpellSuppressionLucky: b.SpellSuppressionLucky, SpellSuppressionUnlucky: b.SpellSuppressionUnlucky,
            SpellSuppressionWhileFullLife: b.SpellSuppressionWhileFullLife,
            SpellSuppressionWhileMoving: b.SpellSuppressionWhileMoving,
            SpellSuppressionIfHitRecently: b.SpellSuppressionIfHitRecently,
            MovementVelocityPct: b.MovementVelocityPct,
            BlockChance: b.BlockChance, SpellBlockChance: b.SpellBlockChance,
            BlockChanceMax: maxBlock, SpellBlockChanceMax: maxSpellBlock, BlockChanceUncapped: b.BlockChance,
            SpellBlockPer5AttackBlock: b.SpellBlockPer5AttackBlock, LifeGainedOnBlock: b.LifeGainedOnBlock,
            ManaGainedOnBlock: b.ManaGainedOnBlock, VersatileCombatant: b.VersatileCombatant,
            AttackBlockLucky: b.AttackBlockLucky, SpellBlockLucky: b.SpellBlockLucky, AttackSpeedPct: attackSpeedPct,
            CriticalStrikeChancePct: b.CriticalStrikeChancePct + b.LightningOvercapCritPct,
            SpellCriticalStrikeChancePct: b.SpellCriticalStrikeChancePct + b.LightningOvercapCritPct,
            CriticalStrikeMultiplier: DamageModel.BaseCriticalStrikeMultiplier + b.CriticalStrikeMultiplier,
            SpellCriticalStrikeMultiplier: DamageModel.BaseCriticalStrikeMultiplier + b.SpellCriticalStrikeMultiplier,
            SpellCriticalStrikeChance: DamageModel.CriticalStrikeChancePerMyriad(
                DamageModel.BaseSpellCriticalStrikeChancePerMyriad,
                b.SpellCriticalStrikeChancePct + b.LightningOvercapCritPct),
            AccuracyPct: b.AccuracyPct,
            AccuracyMoreVsUniquePct: b.AccuracyMoreVsUniquePct,
            AccuracyMoreInCloseRangePct: b.AccuracyMoreInCloseRangePct,
            ManaReservationEfficiencyPct: b.ManaReservationEfficiencyPct,
            LifeReservationEfficiencyPct: b.LifeReservationEfficiencyPct,
            AuraEffectPct: b.AuraEffectPct, AuraAreaPct: b.AuraAreaPct,
            ColdOvercapEvasionPct: b.ColdOvercapEvasionPct, LightningOvercapCritPct: b.LightningOvercapCritPct);
    }

    private static readonly Lazy<uint[]> Experience = new(() => LoadExperience("data"));

    /// <summary>Every level's starting experience (index = level − 1), ExperienceLevels.dat.</summary>
    public static IReadOnlyList<uint> LevelStarts => Experience.Value;

    /// <summary>The experience at the start of a level (ExperienceLevels.dat; level 37: 12 361 842). The client derives
    /// the level from the Player component's experience (04 §4).</summary>
    public static uint ExperienceFor(int level, string folder = "data")
    {
        var table = folder == "data" ? Experience.Value : LoadExperience(folder);
        return level >= 1 && level <= table.Length ? table[level - 1] : 0;
    }

    private static uint[] LoadExperience(string folder)
    {
        string path = Path.Combine(folder, "tables", "ExperienceLevels.json");
        if (!File.Exists(path)) return [0];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. d.RootElement.EnumerateArray().OrderBy(r => r.GetProperty("Level").GetInt32()).Select(r => (uint)r.GetProperty("Experience").GetInt64())];
    }
}
