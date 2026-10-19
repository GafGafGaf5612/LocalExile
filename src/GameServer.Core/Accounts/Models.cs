namespace GameServer.Core.Accounts;

/// <param name="ClientDigestHash">PBKDF2 over the official client's password digest (SHA-256 of the UTF-16LE password),
/// so logins from the real client can be checked; null for accounts made before it existed until their next dev login.</param>
public sealed record Account(Guid Id, string Name, string PasswordHash, string PasswordSalt, DateTimeOffset CreatedUtc,
    string? ClientDigestHash = null);

public enum CharacterClass { Scion, Marauder, Ranger, Witch, Duelist, Templar, Shadow }

/// <param name="AreaId">The dev JSON protocol's current area (GameSession); the official login flow does not use it.</param>
/// <param name="LastTown">WorldAreas Id of the last town the character entered (Auth records it when the Gateway resolves
/// a town ticket). Every official login starts there, as in the real game; null = never entered a town, which logs in at
/// <see cref="AccountService.StartingArea"/>.</param>
public sealed record Character(
    Guid Id, Guid AccountId, string Name, CharacterClass Class, int Level,
    string League, string AreaId, DateTimeOffset CreatedUtc, DateTimeOffset LastPlayedUtc, string? LastTown = null);

public sealed record Session(string Token, Guid AccountId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
