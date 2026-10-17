using System.Net;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Host;
using GameServer.Storage;

// Dev game server: accounts + characters + instances over a JSON-lines TCP protocol.
// Game_DEV_PORT (default 7000), Game_DEV_BIND (default 127.0.0.1), Game_DEV_DB (default data/accounts.json).
int port = int.TryParse(Environment.GetEnvironmentVariable("Game_DEV_PORT"), out var p) ? p : 7000;
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_DEV_BIND") ?? "127.0.0.1");
string db = Environment.GetEnvironmentVariable("Game_DEV_DB") ?? Path.Combine("data", "accounts.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(db))!);

await using var host = new GameHost(new AccountService(new JsonAccountStore(db)), new InstanceManager(), bind, port);
host.Start();
Console.WriteLine($"[host] listening on {bind}:{host.Port}, db={db}");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
await done.Task;
