using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Protocol.Login;

/// <summary>
/// Server-side state of one official-client login connection after the key exchange: decoded C→S messages in,
/// S→C replies out. No sockets or crypto here (GameServer.Auth does those).
/// </summary>
public sealed class LoginServerSession(AccountService accounts)
{
    /// <summary>0x04 result codes. 0 moves the client to logged-in state 3 (0x14213f190 case 4). Non-zero codes are
    /// BackendErrors rows + 1: the client showed 5 as "The specified account does not exist." (row 4, live 2026-10-16),
    /// so 6 is row 5 InvalidPassword.</summary>
    public const ushort ResultOk = 0, ResultAccountDoesNotExist = 5, ResultInvalidPassword = 6;

    /// <summary>Set after a failed login. The client keeps the connection open and ignores Log In until the server closes
    /// it (live 2026-10-16), so the transport should disconnect once the reply is sent.</summary>
    public bool Failed { get; private set; }

    /// <summary>0x04 Flags0 bits (0x14213f190 case 4).</summary>
    public const byte FlagVerified = 0x20;

    public const string League = "Standard";

    public Session? Session { get; private set; }
    public Account? Account { get; private set; }

    public IReadOnlyList<ILoginMessage> Handle(ILoginMessage message) => message switch
    {
        AccountLogin m => Login(m),
        // Sent when the character screen opens (0x140d13060, only in logged-in state 3). Answered with the character
        // list 0x15 (stored at login+0xe68 and shown by the character screen 0x140d12590; live-tested).
        EmptyMessage { MessageId: 0x18 } => [new List15(CharacterEntries(), 0, 0, [])],
        // Play on the character screen sends 0x0e (u8 0, u32 0: character index?; captured 2026-10-16). It waits
        // ("Contacting Server...") for the instance assignment 0x14.
        U8U32 m => EnterGame(m),
        _ => [],
    };

    /// <summary>Where 0x14 sends the client: the instance (game) server, GameServer.Gateway.</summary>
    public string InstanceHost { get; init; } = "127.0.0.1";
    public ushort InstancePort { get; init; } = 6112;

    /// <summary>Derives the 0x14 instance key from the ticket; GameServer.Gateway must use the same secret.</summary>
    public InstanceKeyring Keyring { get; init; } = InstanceKeyring.Random();

    /// <summary>
    /// 0x14 for the account's first character. The area hash is WorldAreas.HASH16 (Lioneye's Watch, 1_1_town = 0x235c
    /// in 3.29.3b); an unknown hash makes the client throw "Area reported by instance does not exist". +0x30 is the port
    /// (live-verified). The client echoes +0x34 (here the IPv4 address) in instance 0x0E and the ticket at +0x38 in
    /// instance 0x0F. The 64 key bytes become the instance connection's Salsa20 key material. Data8 goes
    /// into the 0x0E digest and stays zero.
    /// </summary>
    private ILoginMessage[] EnterGame(U8U32 play)
    {
        if (Session is null) return [];
        var characters = accounts.Characters(Session.AccountId);
        if (characters.Count == 0) return [];
        // The u32 is probably the character's index on the character screen (0 for the first, the only one tested).
        CharacterName = characters[(int)Math.Min(play.Value, (uint)characters.Count - 1)].Name;
        if (Assign is not null)
        {
            var (area, assigned) = Assign(this);
            return [InstanceAssignment(area, assigned)];
        }
        return [InstanceAssignment(0x235c, Keyring.NewTicket())];
    }

    /// <summary>The character chosen with Play (0x0e), once in game.</summary>
    public string? CharacterName { get; private set; }

    /// <summary>Picks the area and ticket for Play (GameServer.Auth: the instance registry). Without it: Lioneye's
    /// Watch with a fresh ticket.</summary>
    public Func<LoginServerSession, (ushort Area, uint Ticket)>? Assign { get; init; }

    /// <summary>0x14 sending the client to <paramref name="areaHash16"/> on the instance server with
    /// <paramref name="ticket"/>. Only at character select: in game the client ignores login 0x14 and area changes use
    /// instance 0x27.</summary>
    public InstanceInfo InstanceAssignment(ushort areaHash16, uint ticket)
    {
        uint ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(System.Net.IPAddress.Parse(InstanceHost).GetAddressBytes());
        return new InstanceInfo(InstanceHost, InstancePort, ip, areaHash16, ticket, Keyring.Key(ticket), new byte[8], 0);
    }

    private ILoginMessage[] Login(AccountLogin m)
    {
        var r = accounts.LoginWithClientDigest(m.Account, m.PasswordDigest);
        if (!r.Ok)
        {
            Failed = true;
            ushort code = r.Error == AuthError.NotFound ? ResultAccountDoesNotExist : ResultInvalidPassword;
            return [new LoginResult(code, "", new byte[32], 0, 0, [])];
        }
        Session = r.Value;
        Account = accounts.GetAccount(Session!.AccountId);
        // The wide string is shown to the player in a message box (live test 2026-10-16: sending the account name popped up
        // "devtester" with an OK button), so it stays empty. Digest, flags and data go to UI listeners; their meaning is
        // unknown, so they are sent empty too.
        // The real client waits for a push after 0x04; an empty 0x1b moves it on to the character screen, which then
        // asks for the character list with 0x18 (captured 2026-10-16). What 0x1b's own entries are is still unknown.
        // Flags0 bit 5 → login+0xea8: the account's email is verified. With it clear the character screen builds the
        // "Your account is not verified" panel (0x140d195d0) and chat/trade features are locked.
        return [new LoginResult(ResultOk, "", new byte[32], FlagVerified, 0, []),
                new CharacterList(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [], false, false)];
    }

    /// <summary>
    /// The account's characters as 0x15 entries. Mapped live (2026-10-16) with one probe character per field set to 3:
    /// +0x48 is the level ("Level 3"), +0x4a the class (3 showed "Witch"; our enum has the same order with Scion = 0),
    /// +0x44 / +0x46 change the portrait, +0x49 adds a red marker. The others showed no visible change.
    /// </summary>
    private ListEntry15[] CharacterEntries() =>
        Session is null ? [] :
        [.. accounts.Characters(Session.AccountId).Select(c =>
            new ListEntry15(c.Name, c.League, 0, 0, (byte)Math.Clamp(c.Level, 0, 255), 0, (byte)c.Class, 0, 0, 0, 0))];
}
