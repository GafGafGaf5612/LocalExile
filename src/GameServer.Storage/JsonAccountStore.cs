using System.Text.Json;
using GameServer.Core.Accounts;

namespace GameServer.Storage;

/// <summary>Durable store: whole state in one JSON file (atomic replace). Pass null path for memory-only.</summary>
public sealed class JsonAccountStore : IAccountStore
{
    private sealed record State(List<Account> Accounts, List<Character> Characters);
    private readonly string? _path;
    private readonly object _gate = new();
    private State _s = new([], []);
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public JsonAccountStore(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path))
            _s = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Opts) ?? _s;
    }

    private void Save()
    {
        if (_path is null) return;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_s, Opts));
        File.Move(tmp, _path, overwrite: true);
    }

    public Account? FindAccount(string name) { lock (_gate) return _s.Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
    public Account? GetAccount(Guid id) { lock (_gate) return _s.Accounts.FirstOrDefault(a => a.Id == id); }
    public void AddAccount(Account a) { lock (_gate) { _s.Accounts.Add(a); Save(); } }
    public void UpdateAccount(Account a) { lock (_gate) { var i = _s.Accounts.FindIndex(x => x.Id == a.Id); if (i >= 0) { _s.Accounts[i] = a; Save(); } } }
    public IReadOnlyList<Character> CharactersOf(Guid id) { lock (_gate) return _s.Characters.Where(c => c.AccountId == id).OrderBy(c => c.CreatedUtc).ToList(); }
    public Character? FindCharacter(string n) { lock (_gate) return _s.Characters.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); }
    public Character? GetCharacter(Guid id) { lock (_gate) return _s.Characters.FirstOrDefault(c => c.Id == id); }
    public void AddCharacter(Character c) { lock (_gate) { _s.Characters.Add(c); Save(); } }
    public void UpdateCharacter(Character c) { lock (_gate) { var i = _s.Characters.FindIndex(x => x.Id == c.Id); if (i >= 0) { _s.Characters[i] = c; Save(); } } }
    public void DeleteCharacter(Guid id) { lock (_gate) { _s.Characters.RemoveAll(c => c.Id == id); Save(); } }
}
