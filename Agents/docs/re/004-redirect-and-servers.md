# Running the private server (redirect + components)

## Components

| Project | Listens | Impersonates | State |
| --- | --- | --- | --- |
| `GameServer.Patch` | `https://0.0.0.0:443` (or `:8080` no-TLS) | `patch.Gamecdn.com` | Running: serves `gateway_list.txt`; logs all requests (index protocol pending) |
| `GameServer.Auth` | `https://0.0.0.0:20484` (or `:20484` no-TLS) | `{realm}.login.Game.com` | Running: TLS + request capture; login protocol pending |
| `GameServer.Gateway` | `tcp 0.0.0.0:6112` | instance servers | Running: accepts + logs raw bytes; game protocol pending |

Start everything:

```powershell
dotnet build GameServer.slnx
./tools/run-servers.ps1            # add -NoTls for plain HTTP testing
```

## Redirecting the client

```powershell
# elevated: edits hosts + machine trust store; generates server/Game-dev.pfx
./tools/redirect/setup-redirect.ps1 -Action install
# ... later ...
./tools/redirect/setup-redirect.ps1 -Action remove
```

How redirection works (from `001-client-endpoints.md` + the live CDN):

1. `patch.Gamecdn.com` is redirected to us. The client fetches `gateway_list.txt`
   from the CDN root; we serve a list whose **login host:port** is `127.0.0.1:20484`.
2. The client's `[LOGIN] gateway_id` (in `production_Config.ini`) selects a line,
   so our list keeps `Europe`/`America` ids.
3. Our TLS cert is trusted machine-wide, so libcurl accepts `patch.Gamecdn.com`
   and the login host.

### gateway_list.txt format (captured from the live CDN)

UTF-16LE, BOM `FF FE`, CRLF, one line per gateway:

```
<id>;<display>;<login-host:port>;<speedtest-host>;<bool>
```

Example (ours, `server/gateway_list.txt`):

```
Europe;Local (EU);127.0.0.1:20484;127.0.0.1;false
America;Local (US);127.0.0.1:20483;127.0.0.1;false
```

## Version note (important)

The installed `Game.exe` **self-updated from `3.28.0f` to `3.29.3b`** when
launched during reconnaissance (exe mtime changed, now contains `tags/3.29.3b`).
The `Content.ggpk` data is still `3.28.x`. The client and data must be the **same**
version for a coherent private server. Options: re-patch data to match the exe, or
restore/pin the client to the version whose data you have.

## Proven vs pending

- **Proven:** all three components run and capture; TLS termination works; the
  `gateway_list.txt` redirect payload is byte-correct vs the live CDN.
- **Pending (needs live capture through the redirect above):** the patch-index
  request format, the login request/response shapes, and the `:6112` game protocol.
- **Key risk:** if the client **pins** the patch/login TLS certificate, the trusted
  cert will not be enough and a binary patch (REA/Ghidra) is required.

## Capturing (once redirected)

Run the servers, launch the client, and inspect the console/log output plus
`Client/logs/LatestClient.txt`. The servers log every request/response candidate
and the raw first bytes of every `:6112` connection.
