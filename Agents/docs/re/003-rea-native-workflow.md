# M2 — REA / Ghidra native-analysis workflow

## Setup (verified)

- REA `6.4.0` (`rea` on PATH), registered with opencode (`mcp.rea` in `~/.config/opencode/opencode.jsonc`).
- Provider: Ghidra `12.1.4` (Windows x64 P0, read-only) at
  `Tools/ghidra_12.1.4_PUBLIC_20260921/ghidra_12.1.4_PUBLIC`.
- JDK 21 (Microsoft OpenJDK `21.0.12.1`, 64-bit) at `Tools/jdk-21`.
- `rea doctor --provider ghidra` → healthy (platform win32, arch x64, native decompiler `win_x86_64`, JDK ok).

Provider environment used for CLI/MCP:

```
GHIDRA_INSTALL_DIR=<repo>\Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC
JAVA_HOME=<repo>\Tools\jdk-21
REA_ANALYSIS_PROVIDER=ghidra
REA_GHIDRA_STARTUP_TIMEOUT_MS=<ms>   # raise for large targets
```

## Result: pipeline validated, big client needs a bigger budget

| Target | Size | Result |
| --- | --- | --- |
| `Client/PackCheck.exe` | 1.29 MB | **OK** — 8 segments, 3,286 procedures, 2,712 strings (~110 s) |
| `Client/Game.exe` | 73.7 MB | `provider_timeout` at 1,200,000 ms (`list_documents`) |

Command that worked:

```powershell
$env:GHIDRA_INSTALL_DIR = "...\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC"
$env:JAVA_HOME = "...\Tools\jdk-21"
$env:REA_ANALYSIS_PROVIDER = "ghidra"
rea analyze "Client\PackCheck.exe" --provider ghidra --format json
```

## Analyzing the full client

The main client is too large for the default cold-import window. Recommended approaches, in order:

1. **Raise the budget and use a snapshot**: set `REA_GHIDRA_STARTUP_TIMEOUT_MS` to e.g. `3600000` (1 h), run a first `rea analyze`, and persist with `--snapshot <file>` so subsequent queries reuse the database instead of re-importing.
2. **Focus the target**: analyze extracted regions or the launcher/aux DLLs first; for whole-program work prefer the snapshot workflow so a single long import amortizes across many queries.
3. **Start with a focused query**: `rea function <path> <address|name>` still requires the import, so the snapshot route matters most.

Windows Ghidra P0 is read-only, x86/x86-64 PE only, and does not expose annotations — sufficient for endpoint/host-synthesis RE, which pairs with the string evidence in `001-client-endpoints.md`.

## What to look for next (feeds H4.1/H4.2)

- xrefs to `GGG_BASE_WEB_CDN_URL` / `GGG_LOGIN_SERVER_PORT` → the config lookup path.
- xrefs to the `.Game.com` literal (offset `0x03393618`) → the `{realm}.login.Game.com` host builder.
- the gateway_list.txt parser (string `02FCC148`) → the list file format the patch server must serve.
