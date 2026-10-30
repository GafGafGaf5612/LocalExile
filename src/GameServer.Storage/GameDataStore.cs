using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// The SQLite overlay for server-side game data: mod stat roll ranges and item limits. The extracted
/// <c>data/tables</c> JSON stays the reference; this store only records overrides the Gateway applies in memory at
/// startup and after a command. One connection is shared by the Gateway's writers and readers and every write is a
/// transaction; the schema lives beside the save tables in <c>data/saves.db</c> (or <c>Game_SAVES_DB</c>).
/// </summary>
public sealed class GameDataStore : IDisposable
{
    public sealed record ModStatOverride(string ModId, int StatIndex, int Minimum, int Maximum);
    public sealed record LimitOverride(string Key, int Value);
    public sealed record CatalogRow(string Kind, string Key, string Payload, bool Enabled);
    public sealed record SettingRow(string Key, double Value);

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private SqliteTransaction? _tx;

    public string Path { get; }

    public GameDataStore(string path)
    {
        Path = path;
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        try
        {
            Execute("PRAGMA busy_timeout=10000");
            Execute("""
                CREATE TABLE IF NOT EXISTS game_data_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS mod_stat_overrides (
                    mod_id TEXT NOT NULL,
                    stat_index INTEGER NOT NULL,
                    minimum INTEGER NOT NULL,
                    maximum INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (mod_id, stat_index),
                    CHECK (minimum <= maximum)
                ) STRICT;
                CREATE TABLE IF NOT EXISTS limit_overrides (
                    key TEXT PRIMARY KEY,
                    value INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    CHECK (value >= 0)
                ) STRICT;
                CREATE TABLE IF NOT EXISTS system_settings (
                    key TEXT PRIMARY KEY,
                    value REAL NOT NULL,
                    updated_utc TEXT NOT NULL
                ) STRICT;
                CREATE TABLE IF NOT EXISTS catalog_entries (
                    kind TEXT NOT NULL,
                    key TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (kind, key)
                ) STRICT;
                CREATE TABLE IF NOT EXISTS item_templates (
                    id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    base_type TEXT NOT NULL,
                    rarity INTEGER NOT NULL,
                    item_level INTEGER NOT NULL,
                    quality INTEGER NOT NULL,
                    custom_name TEXT,
                    name_words TEXT,
                    visual INTEGER,
                    corrupted INTEGER NOT NULL,
                    mirrored INTEGER NOT NULL,
                    mods TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                ) STRICT;
                """);
            Execute("INSERT OR IGNORE INTO game_data_meta (key, value) VALUES ('schema_version', '1')");
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ModStatOverride> LoadModRanges()
    {
        lock (_gate)
        {
            using var command = Command("SELECT mod_id, stat_index, minimum, maximum FROM mod_stat_overrides ORDER BY mod_id, stat_index");
            using var reader = command.ExecuteReader();
            var result = new List<ModStatOverride>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            return result;
        }
    }

    public void SetModRange(string modId, int statIndex, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("mod id is empty", nameof(modId));
        if (statIndex < 0) throw new ArgumentOutOfRangeException(nameof(statIndex));
        if (minimum > maximum) throw new ArgumentException("minimum is above maximum");
        Atomically(() =>
        {
            using var command = Command("""
                INSERT INTO mod_stat_overrides (mod_id, stat_index, minimum, maximum, updated_utc)
                VALUES ($mod, $index, $minimum, $maximum, $utc)
                ON CONFLICT(mod_id, stat_index) DO UPDATE SET
                    minimum = excluded.minimum,
                    maximum = excluded.maximum,
                    updated_utc = excluded.updated_utc
                """, ("$mod", modId), ("$index", statIndex), ("$minimum", minimum), ("$maximum", maximum),
                ("$utc", DateTime.UtcNow.ToString("O")));
            command.ExecuteNonQuery();
        });
    }

    public void ClearModRange(string modId, int? statIndex = null)
    {
        Atomically(() =>
        {
            using var command = statIndex is int index
                ? Command("DELETE FROM mod_stat_overrides WHERE mod_id = $mod AND stat_index = $index", ("$mod", modId), ("$index", index))
                : Command("DELETE FROM mod_stat_overrides WHERE mod_id = $mod", ("$mod", modId));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<LimitOverride> LoadLimits()
    {
        lock (_gate)
        {
            using var command = Command("SELECT key, value FROM limit_overrides ORDER BY key");
            using var reader = command.ExecuteReader();
            var result = new List<LimitOverride>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    public void SetLimit(string key, int value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("limit key is empty", nameof(key));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Atomically(() =>
        {
            using var command = Command("""
                INSERT INTO limit_overrides (key, value, updated_utc)
                VALUES ($key, $value, $utc)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc
                """, ("$key", key), ("$value", value), ("$utc", DateTime.UtcNow.ToString("O")));
            command.ExecuteNonQuery();
        });
    }

    public void ClearLimit(string key)
    {
        Atomically(() =>
        {
            using var command = Command("DELETE FROM limit_overrides WHERE key = $key", ("$key", key));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<SettingRow> LoadSettings()
    {
        lock (_gate)
        {
            using var command = Command("SELECT key, value FROM system_settings ORDER BY key");
            using var reader = command.ExecuteReader();
            var result = new List<SettingRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetDouble(1)));
            return result;
        }
    }

    public void SetSetting(string key, double value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("setting key is empty", nameof(key));
        if (!double.IsFinite(value)) throw new ArgumentException("setting value is not finite", nameof(value));
        Atomically(() =>
        {
            using var command = Command("""
                INSERT INTO system_settings (key, value, updated_utc)
                VALUES ($key, $value, $utc)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc
                """, ("$key", key), ("$value", value), ("$utc", DateTime.UtcNow.ToString("O")));
            command.ExecuteNonQuery();
        });
    }

    public void DeleteSetting(string key)
    {
        Atomically(() =>
        {
            using var command = Command("DELETE FROM system_settings WHERE key = $key", ("$key", key));
            command.ExecuteNonQuery();
        });
    }

    public bool SettingsSeeded() => Meta("settings_seeded") is not null;

    public void MarkSettingsSeeded() => SetMeta("settings_seeded", DateTime.UtcNow.ToString("O"));

    public IReadOnlyList<CatalogRow> LoadCatalog(string kind)
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, key, payload, enabled FROM catalog_entries WHERE kind = $kind ORDER BY key",
                ("$kind", kind));
            using var reader = command.ExecuteReader();
            var result = new List<CatalogRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            return result;
        }
    }

    public void PutCatalogRow(string kind, string key, string payload, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("catalog kind is empty", nameof(kind));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("catalog key is empty", nameof(key));
        Atomically(() =>
        {
            using var command = Command("""
                INSERT INTO catalog_entries (kind, key, payload, enabled, updated_utc)
                VALUES ($kind, $key, $payload, $enabled, $utc)
                ON CONFLICT(kind, key) DO UPDATE SET
                    payload = excluded.payload,
                    enabled = excluded.enabled,
                    updated_utc = excluded.updated_utc
                """, ("$kind", kind), ("$key", key), ("$payload", payload), ("$enabled", enabled ? 1 : 0),
                ("$utc", DateTime.UtcNow.ToString("O")));
            command.ExecuteNonQuery();
        });
    }

    public void DeleteCatalogRow(string kind, string key)
    {
        Atomically(() =>
        {
            using var command = Command("DELETE FROM catalog_entries WHERE kind = $kind AND key = $key",
                ("$kind", kind), ("$key", key));
            command.ExecuteNonQuery();
        });
    }

    public bool CatalogSeeded(string kind) => Meta("catalog_seeded:" + kind) is not null;

    public void MarkCatalogSeeded(string kind) => SetMeta("catalog_seeded:" + kind, DateTime.UtcNow.ToString("O"));

    public string? Meta(string key)
    {
        lock (_gate)
        {
            using var command = Command("SELECT value FROM game_data_meta WHERE key = $key", ("$key", key));
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        Atomically(() =>
        {
            using var command = Command("""
                INSERT INTO game_data_meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """, ("$key", key), ("$value", value));
            command.ExecuteNonQuery();
        });
    }

    private void Atomically(Action work)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            _tx = transaction;
            try
            {
                work();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                _tx = null;
            }
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
