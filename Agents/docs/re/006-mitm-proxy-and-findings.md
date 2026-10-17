# Non-elevated redirect (MITM proxy) + pass-through CDN + findings

## What was built this cycle

1. **`GameMitmProxy`** (`tools/GameMitmProxy`): a small MITM that exploits the fact
   that **the client's libcurl honors `HTTPS_PROXY`**. It accepts `CONNECT`,
   terminates TLS with the (already machine-trusted) `server/Game-dev.pfx`, and relays
   the decrypted HTTP/1.1 stream to local backends:
   - `patch.Gamecdn.com` → `GameServer.Patch`
   - `*.login.Game.com` → `GameServer.Auth`

   This gives a **redirect with no hosts edit and no elevation**.

2. **`GameServer.Patch` is now a selective CDN front-end**: it serves
   `gateway_list.txt` itself (pointing login at the local Auth server) and
   **passes every other request through to the real CDN** (`patch.Gamecdn.com`),
   forwarding the client's Basic auth. This lets a client with stale local data
   repair itself transparently.

## Confirmed protocol facts (3.29.3b / patch 3.29.3.3)

- Patch transport: HTTPS, one GET per file, `Authorization: Basic` (the official CDN credential, not reproduced).
- Login host:port comes from `gateway_list.txt` (UTF-16LE) served by the CDN root.
- The local `Content.ggpk` is genuine 3.29.3.3 for most files (e.g. `Tiny_5` matched
  the CDN byte-for-byte) but was **stale/corrupt for a subset** (e.g.
  `.../microtransactions_92.bundle.bin`). The pass-through repaired that file
  (`match=True` after), confirming the mechanism.

## Hard environmental limits (blocks "reach login" in this session)

- **No GPU/display**: the client finishes "checking files" and then stalls before
  engine init — it never logs `Async connecting to …login…`, so the login request
  never happens. Running the game to the login screen requires a real desktop
  session.
- **UAC**: the elevated hosts-file route was declined; that is why the MITM proxy
  (no elevation) was built instead.

## To capture login (run this on your desktop)

```powershell
# 1. build
dotnet build GameServer.slnx
# 2. start backends + mitm (see tools/run-servers.ps1 and tools/GameMitmProxy)
# 3. in the SAME shell, launch the client with the proxy:
$env:HTTPS_PROXY='http://127.0.0.1:8899'; $env:HTTP_PROXY='http://127.0.0.1:8899'; $env:NO_PROXY=''
$env:SSL_CERT_FILE=(Resolve-Path 'server/Game-dev.pem'); $env:CURL_CA_BUNDLE=$env:SSL_CERT_FILE
Start-Process 'Client\Game.exe' -WorkingDirectory Client
```

With a display, the client should pass patching and open the login screen; the
`GameServer.Auth` log will then contain the login request/response shapes to
implement. This is the remaining step before auth/character/instance work.

## Client state

`Content.ggpk` ~70.6 GB, executable 3.29.3b, `PackCheck.exe` restored, redirect
removed (hosts clean, no proxy env persisted). A subset of files may still be stale;
running the official launcher with the redirect off will verify/repair to a single
3.29.3.3 state.
