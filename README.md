# Human-Edit

Hey, I hope this doesnt go viral and GGG sues me. GGG can reach out to me if they want this repo gone and I'll act on it. 
This is for fun only and for local-gameplay with no internet. Lastly, I'm a human and I can make mistakes, warn me instead.

Everything here is made by Claude Opus 5.5. You can pull-request and are encouraged to. I'm running only on two pro subs
and Im broke and unemployed, so help out folks.

# LocalExile

A local, from-scratch server for the Path of Exile **3.29.3b** client. It is written in C# (.NET 10) from reverse
engineering of the client's network protocol. You play on your own machine against servers running on 127.0.0.1.

This project is not affiliated with or endorsed by Grinding Gear Games. It contains **no game files, keys or game data**.
You need your own installed copy of the client, and everything the servers need is generated locally from it.

## Showcase

https://github.com/user-attachments/assets/ebb49e70-93aa-464f-a6cd-c6c130dd8c5a

Character select, Lioneye's Watch, taking The Coast exit,
and `/set_speed` in The Coast, all against the local servers.

## What works

- **Login:** the client's custom TCP login (Diffie-Hellman key exchange, DSA-signed server key, Salsa20), accounts and
  the character list.
- **Instance server (Gateway):** encryption, area load, player object, inventories, latency acks, visibility and
  click-to-move.
- **Lioneye's Watch → The Coast:** the exit loads the area. Ctrl+click opens the Instance Manager (list, join or create
  instances). Towns have one instance, and empty instances expire after 6 minutes.
- **Chat commands:** `/help`, `/set_speed <percent>`, `/set_stat <key> <value>`, `/raw <hex>`.

Not done yet: monsters (`/spawn_mob` is a stub), server text as chat lines, exit to character selection, and exits
beyond town.

## Projects

| Project | Role | Port |
|---|---|---|
| `GameServer.Auth` | login server; also generates keys and patches the client to trust them | 20493 (login), 127.0.0.1:20495 (control) |
| `GameServer.Gateway` | instance (game) server | 6112 |
| `GameServer.Patch` | local patching server, so the client never contacts the real patch servers; also edits the GGPK gateway list | 12995, http 8080 |
| `GameServer.Host` + `GameServer.DevClient` | dev account service (register accounts, create characters) | 7000 |
| `GameServer.Core` | protocol, crypto, accounts, instance registry | |
| `GameServer.Data` | GGPK / bundle / `.dat` readers | |
| `GameServer.Storage` | JSON account store | |

## Getting Started

### 1. Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- PowerShell 5.1+ (for `scripts/dev/live.ps1`)
- An installed Path of Exile client at **exactly version 3.29.3b**. The login patch works only on that build.

### 2. Clone and build

```powershell
git clone <this repository URL> LocalExile
cd LocalExile
dotnet build GameServer.slnx
dotnet test tests/GameServer.Core.Tests
```

### 3. Put a copy of the client in `Client/`

Copy the whole client folder into `Client/`. Work on a copy: the next steps modify `Content.ggpk`. Then copy the
client's main executable to the name the tools use:

```powershell
Copy-Item Client\PathOfExile.exe Client\Game.exe
```

`Client/` is git-ignored.

### 4. Generate your own login keys and patch the client

```powershell
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
```

- `keygen` writes `data/login-keys.json`: a new server signing key over the client's own DSA parameters.
- `patch` writes `Client/Game.patched.exe`, a copy of the client that trusts that key instead of the official one.

The instance key secret (`data/instance-secret.bin`) is created automatically on first start. Everything under `data/`
is git-ignored. Never commit it.

### 5. Point the client's gateway list at the local login server

```powershell
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
```

This rewrites `gateway_list.txt` inside `Client/Content.ggpk` and keeps a backup in `data/gateway_list.original.txt`.

### 6. Create an account and a character

In one terminal:

```powershell
dotnet run --project src/GameServer.Host
```

In another:

```powershell
dotnet run --project src/GameServer.DevClient
```

Then type:

```
register name=devtester password=<choose one>
login name=devtester password=<the same>
create_character name=DevWitch class=witch
```

Accounts are stored in `data/accounts.json`. Stop `GameServer.Host` afterwards.

### 7. Run

```powershell
$env:Game_GATEWAY_ENTER = '1'
.\scripts\dev\live.ps1
```

`live.ps1`:
1. stops anything already on the dev ports;
2. builds Auth, Patch and Gateway and starts them (logs in `logs/live-*.log`);
3. waits for the patch server, then starts `Client/Game.patched.exe --patching-server 127.0.0.1:12995`.

Log in with the account from step 6 and press Play. You arrive in Lioneye's Watch.

Useful switches:
- `-NoClient` restarts only the servers and keeps the running client.
- `-NoBuild` skips the builds.

## Important

- **Always start the client through `live.ps1`.** Started any other way, the client contacts the official patch
  servers and may update itself. An update overwrites the patched executable and data.
- Run only one client at a time.
- The Gateway reads a few protocol constants from your local `Client/Game.exe` at startup. They are checked by hash
  and never stored in this repository.
- Walkability grids are optional. Without `data/walkability/1_1_town.bin` (a 690×414 byte grid dumped from the
  client), the Gateway assumes every cell is walkable when it estimates the player's position.
- The `GameServer.Data` readers use the community [`poe-tool-dev/dat-schema`](https://github.com/poe-tool-dev/dat-schema)
  (`schema.min.json`). Put it in `data/schema/` if you use them; it is not included here.

## Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `Game_LOGIN_PORT` | 20493 | login server port |
| `Game_GATEWAY_PORT` | 6112 | instance server port |
| `Game_GATEWAY_HOST` | 127.0.0.1 | address sent to the client for area changes |
| `Game_GATEWAY_ENTER` | unset | `1` = enter the area after creating the player |
| `Game_AUTH_CONTROL_PORT` | 20495 | Auth ↔ Gateway control port (localhost only) |
| `Game_RANDOM_SEEDS` | unset | `1` = random seeds for new non-town instances (needs matching walkability dumps) |
| `Game_MOVE_MODE` | echo | `echo`, `move` or `teleport` |
| `Game_WALK_SPEED` | 37 | walk speed in grid cells per second, used for position estimates |
| `Game_CLIENT_EXE` | Client/Game.exe | client executable the Gateway reads protocol constants from |
| `Game_DEV_DB` | data/accounts.json | account store |
| `Game_INSTANCE_SECRET` | data/instance-secret.bin | instance key secret |
