namespace GameServer.Core.Accounts;

/// <param name="ClientDigestHash">PBKDF2 over the official client's password digest (SHA-256 of the UTF-16LE password),
/// so logins from the real client can be checked; null for accounts made before it existed until their next dev login.</param>
public sealed record Account(Guid Id, string Name, string PasswordHash, string PasswordSalt, DateTimeOffset CreatedUtc,
    string? ClientDigestHash = null);

public enum CharacterClass { Scion, Marauder, Ranger, Witch, Duelist, Templar, Shadow }

public sealed record Character(
    Guid Id, Guid AccountId, string Name, CharacterClass Class, int Level,
    string League, string AreaId, DateTimeOffset CreatedUtc, DateTimeOffset LastPlayedUtc);

public sealed record Session(string Token, Guid AccountId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
