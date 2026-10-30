using GameServer.Core.World;
using GameServer.Storage;

/// <summary>
/// Numeric server settings that used to be constants in code. The SQLite <c>system_settings</c> table is seeded with
/// the defaults on the first run; the in-memory snapshot is replaced on <c>/game_data reload</c>. Unknown keys fall back
/// to the seed, so deleting a row restores the code default.
/// </summary>
sealed class SystemSettings
{
    public static readonly IReadOnlyDictionary<string, double> Seeds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["socket_max_wand"] = 3,
        ["socket_max_one_hand_sword"] = 3,
        ["socket_max_thrusting_one_hand_sword"] = 3,
        ["socket_max_one_hand_axe"] = 3,
        ["socket_max_one_hand_mace"] = 3,
        ["socket_max_sceptre"] = 3,
        ["socket_max_dagger"] = 3,
        ["socket_max_rune_dagger"] = 3,
        ["socket_max_claw"] = 3,
        ["socket_max_two_hand_sword"] = 6,
        ["socket_max_two_hand_axe"] = 6,
        ["socket_max_two_hand_mace"] = 6,
        ["socket_max_bow"] = 6,
        ["socket_max_staff"] = 6,
        ["socket_max_warstaff"] = 6,
        ["socket_max_body_armour"] = 6,
        ["socket_max_helmet"] = 4,
        ["socket_max_gloves"] = 4,
        ["socket_max_boots"] = 4,
        ["socket_max_shield"] = 3,
        ["socket_max_belt"] = 0,
        ["passive_points_per_level"] = 1,
        ["gold_drop_multiplier"] = 1,
    };

    static readonly Dictionary<string, string> SocketKeys = new(StringComparer.Ordinal)
    {
        ["Wand"] = "socket_max_wand",
        ["One Hand Sword"] = "socket_max_one_hand_sword",
        ["Thrusting One Hand Sword"] = "socket_max_thrusting_one_hand_sword",
        ["One Hand Axe"] = "socket_max_one_hand_axe",
        ["One Hand Mace"] = "socket_max_one_hand_mace",
        ["Sceptre"] = "socket_max_sceptre",
        ["Dagger"] = "socket_max_dagger",
        ["Rune Dagger"] = "socket_max_rune_dagger",
        ["Claw"] = "socket_max_claw",
        ["Two Hand Sword"] = "socket_max_two_hand_sword",
        ["Two Hand Axe"] = "socket_max_two_hand_axe",
        ["Two Hand Mace"] = "socket_max_two_hand_mace",
        ["Bow"] = "socket_max_bow",
        ["Staff"] = "socket_max_staff",
        ["Warstaff"] = "socket_max_warstaff",
        ["Body Armour"] = "socket_max_body_armour",
        ["Helmet"] = "socket_max_helmet",
        ["Gloves"] = "socket_max_gloves",
        ["Boots"] = "socket_max_boots",
        ["Shield"] = "socket_max_shield",
        ["Belt"] = "socket_max_belt",
    };

    static readonly IReadOnlyDictionary<string, double> Empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public static SystemSettings Default { get; } = new();

    IReadOnlyDictionary<string, double> _values = Empty;

    public IReadOnlyDictionary<string, double> Values => Volatile.Read(ref _values);

    public double Get(string key) =>
        Values.TryGetValue(key, out double value) ? value : Seeds.GetValueOrDefault(key, 0);

    public int GetInt(string key) => (int)Math.Round(Get(key), MidpointRounding.AwayFromZero);

    public int SocketMax(string itemClass) =>
        SocketKeys.TryGetValue(itemClass, out string? key)
            ? Math.Clamp(GetInt(key), 0, SocketLayout.MaximumSockets)
            : 0;

    public int PassivePointsPerLevel => Math.Clamp(GetInt("passive_points_per_level"), 1, 100);

    public double GoldDropMultiplier => Math.Max(0, Get("gold_drop_multiplier"));

    public void Replace(IReadOnlyDictionary<string, double> values) =>
        Volatile.Write(ref _values, new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase));

    public static void ResetToSeeds()
    {
        Default.Replace(Seeds);
        Progression.PassivePointsPerLevel = 1;
    }

    public static void Reload(GameDataStore store)
    {
        if (!store.SettingsSeeded())
        {
            var existing = store.LoadSettings().Select(row => row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in Seeds)
                if (!existing.Contains(key)) store.SetSetting(key, value);
            store.MarkSettingsSeeded();
        }
        var loaded = store.LoadSettings().ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        Default.Replace(loaded);
        Progression.PassivePointsPerLevel = Default.PassivePointsPerLevel;
    }

    public static bool Validate(string key, double value, out string? error)
    {
        error = null;
        if (!Seeds.ContainsKey(key))
        {
            error = $"unknown setting \"{key}\"";
            return false;
        }
        if (key.StartsWith("socket_max_", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0 || value > SocketLayout.MaximumSockets || value != Math.Round(value))
                error = $"{key} is an integer 0..{SocketLayout.MaximumSockets}";
        }
        else if (key.Equals("passive_points_per_level", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 1 || value > 100 || value != Math.Round(value))
                error = "passive_points_per_level is an integer 1..100";
        }
        else if (key.Equals("gold_drop_multiplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 10000)
                error = "gold_drop_multiplier is a number 0..10000";
        }
        return error is null;
    }
}
