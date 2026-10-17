# Path of Exile 3.28.0f — Private Server: Design Spec

- **Date:** 2026-10-12
- **Status:** Draft — awaiting user review
- **Target client:** Path of Exile `3.28.0f`, patch `3.28.0.8` (Windows x64 standalone, GGPK install)
- **Version note (2026-10-12):** during reconnaissance the installed client self-updated its executable to `3.29.3b` (patch `3.29.3.3`); the `Content.ggpk` data remains `3.28.x`. Client and data must be pinned to the same version. Protocol work targets whichever version is pinned (see `Agents/docs/re/004-redirect-and-servers.md`).
- **Goal:** Full private-server emulation of the Path of Exile client, delivered in phases.

## 1. Purpose & scope

Build a self-hosted server stack that a locally patched Path of Exile client can connect
to, authenticate against, and play on. The end state ("full emulation") covers every
server role the client talks to, the game simulation (worlds, entities, combat, items,
skills, progression), and persistence.

This is a research/interoperability/preservation project on software we own. REA's
disclaimer (lawful research, interoperability, preservation) governs the reverse-
engineering workstreams. The private server is intended for local/offline and private
use, not for distribution or interference with live GGG services.

### Confirmed environment facts (evidence)

| Fact | Source |
| --- | --- |
| Client version `3.28.0f`, patch `3.28.0.8` | `Client/logs/LatestClient.txt`: `Game release/tags/3.28.0f Windows x64`, `Web root: https://patch.Gamecdn.com/3.28.0.8/` |
| GGPK container | `Content.ggpk` first bytes `1C 00 00 00 "GGPK"` (63 GB) |
| Login/auth endpoint | `ams.login.Game.com:20484`, `par.login.Game.com:20493` (TLS, HTTP/2 via cURL/libcurl) |
| Instance endpoint | raw TCP `:6112` at rotating per-realm IPs |
| Patch protocol | version 7, root/folder listings, `gateway_list.txt`, bundle downloads |
| Main client binary | `Game.exe` (~73 MB x64); `Game_x64.exe`/`Client.exe` are launcher shims |
| Ghidra 12.1.4 + JDK 21 available | `Tools/`, REA `doctor` healthy |
| REA 6.4.0 registered with opencode | `~/.config/opencode/opencode.jsonc` |

## 2. Decisions (locked)

- **Language/stack:** C# / .NET 10 for all servers and tools.
- **Data strategy:** Reuse proven OSS — `LibGGPK3`/`LibBundle3` (GGPK/bundle/Oodle),
  `Game-tool-dev/dat-schema` (`.dat64` schemas), `ReGame`/`PyGame` as references.
- **Client strategy:** Patch the client (endpoints, cert/hostname checks, patch-server
  bypass) with a small launcher; no reliance on live GGG infrastructure.
- **RE tooling:** REA MCP + Ghidra (Windows P0, read-only) for static analysis; packet
  capture for dynamic protocol structure.
- **Delivery:** Phased. Phase 1 target = end-to-end connectivity (client reaches
  character-select against our servers). Later phases add gameplay.

## 3. Architecture

### 3.1 Server topology (mirrors what the client expects)

| Component | Replaces | Transport | Responsibility |
| --- | --- | --- | --- |
| `GameServer.Patch` | `patch.Gamecdn.com` | HTTPS | Serve a minimal "already current" manifest so the client skips patching and reads `gateway_list.txt`. |
| `GameServer.Auth` | `*.login.Game.com:20484` | TLS / HTTP2 | Realm list, login/auth, character list, instance allocation. |
| `GameServer.Gateway` | `*:6112` | raw TCP | Authoritative game protocol; entity/world/zone messaging. |
| `GameServer.Core` | — | — | Shared protocol codec, framing, crypto/obfuscation, packet dispatch. |
| `GameServer.Data` | — | — | Typed game-data model loaded from extracted `.dat64` + assets index. |
| `GameServer.Sim` | — | — | Worlds/instances, entities, combat, items, skills, AI. |
| `GameServer.Storage` | — | — | Accounts, characters, stashes, persistence. |

### 3.2 Repository layout

```
GameServer.sln
  src/
    GameServer.Core/          # protocol codec, framing, crypto, net abstractions
    GameServer.Patch/         # CDN/patch emulation (ASP.NET Core)
    GameServer.Auth/          # login/realm server (ASP.NET Core)
    GameServer.Gateway/       # instance/game server (raw TCP :6112)
    GameServer.Data/          # game-data model + loaders
    GameServer.Sim/           # world/instance simulation
    GameServer.Storage/       # persistence
  tools/
    GameDataExtractor/        # LibGGPK3 -> bundles -> .dat64 -> JSON/C#
    GameClientPatch/          # binary patch + launcher
    GameProtocolCapture/      # pcap/loopback capture + decoder scratch
  tests/
  data/                      # extracted game data (gitignored)
  Agents/docs/
```

### 3.3 Data flow

1. **Extraction:** `GameDataExtractor` reads `Content.ggpk` via LibGGPK3 → bundle index →
   `.dat64` tables → schema-driven decode (dat-schema) → `data/*.json` (+ generated C#
   row types). Assets index (paths → bundle offsets) is retained for later streaming.
2. **Server load:** `GameServer.Data` loads `data/` at startup into typed collections used
   by `Sim` and by protocol handlers.
3. **Runtime:** Client ↔ `Auth` (HTTP2/TLS) for realm/character/instance allocation;
   client ↔ `Gateway` (`:6112`) for gameplay; both via `Core` codecs.

## 4. Protocol reverse-engineering workstream

The protocol is the central unknown and the gating risk. Method:

1. **Static (REA + Ghidra):** locate endpoint strings, hostname/cert validation, TLS
   pinning, patch-server logic, and the send/receive packet builders in `Game.exe`.
   Windows Ghidra P0 is read-only and x64-PE-only — sufficient for a first pass.
2. **Dynamic (capture):** record login (`:20484`) and instance (`:6112`) sessions to
   characterize framing, encryption/obfuscation, opcode ranges, and message cadence.
3. **Spec:** produce a written protocol spec (framing, crypto, opcode table) as the
   contract `GameServer.Core` implements, with tests asserting byte-exact round-trips on
   captured samples.

## 5. Client patching workstream

- Identify and neutralize: hardcoded login/patch hostnames, certificate/key pinning,
  patch-version self-check, and bundle/hash integrity checks (`HashCache.dat`, `PackCheck.exe`).
- Preferred approach: a launcher that applies minimal in-memory patches at process start
  (avoids on-disk anti-tamper churn), with an on-disk patch as fallback.
- Redirect DNS/hosts as a secondary aid so any unpatched path resolves locally.

## 6. Phasing / milestones

- **M0 — REA online (done).** JDK 21, REA 6.4.0, Ghidra provider healthy, opencode
  registration + skill installed.
- **M1 — Data pipeline.** `GameDataExtractor` emits schema-decoded `.dat64` for the
  3.28.0f GGPK; `GameServer.Data` loads it. Deliverable: parsed tables + typed model.
- **M2 — Protocol recon.** Written protocol spec for login + gateway from static RE and
  capture. Deliverable: protocol spec + captured-sample corpus.
- **M3 — Connectivity.** `GameServer.Patch` + `GameServer.Auth` + stub `Gateway`; patched
  client reaches character-select. Deliverable: end-to-end connectivity demo.
- **M4 — Instances.** Enter a world/instance; terrain + entities streamed from extracted
  data. Deliverable: walkable instance.
- **M5+ — Simulation.** Items, skills, combat, progression, chat/social; persistence.

Each milestone gets its own implementation plan (via the writing-plans skill) before code.

## 7. Error handling & observability

- Servers log structured events (connection lifecycle, opcode dispatch, decode failures)
  with correlation IDs; malformed packets are recorded and dropped, never crash the loop.
- Protocol decoder failures produce a captured byte dump for offline analysis.
- Unknown opcodes are logged with hex + direction so the spec can be extended.

## 8. Testing strategy

- **Data:** golden tests — re-extract a fixed set of tables and assert stable digests.
- **Protocol:** round-trip encode/decode against captured byte samples; opcode-table
  coverage tests.
- **Servers:** integration tests spinning up Patch/Auth/Gateway on loopback with a scripted
  client that replays captured handshakes.
- **Client:** manual acceptance per milestone (real client connects to loopback stack).

## 9. Risks & mitigations

| Risk | Severity | Mitigation |
| --- | --- | --- |
| Certificate/key pinning in login client | High | De-risk first in M2; patch-pinning targets identified via REA/Ghidra. |
| Protocol encryption/obfuscation on `:6112` | High | Static + dynamic recon before implementation; captured-sample round-trips. |
| Anti-tamper (`HashCache.dat`, `PackCheck.exe`, bundle hash renewal) | Medium | Prefer in-memory patching; LibBundle3 exposes `renewHashes()` semantics. |
| 63 GB GGPK processing time/disk | Medium | Streaming extraction; extract only needed tables early. |
| Live-file version drift | Medium | Pin to 3.28.0f; keep schema/version handling explicit. |
| Scope: full combat/skills/endgame simulation | High | Strict phasing; each phase independently useful. |
| Windows Ghidra is read-only P0 | Low | Sufficient for static pass; pair with dynamic capture. |
| Legal/ToS | Medium | Local/private use only; REA disclaimer scope. |

## 10. Out of scope (this spec)

- Distribution, monetization, or connecting to live GGG services.
- Anti-cheat evasion beyond making the local client talk to local servers.
- Client patches unrelated to server redirection.

## 11. Open questions

- Exact login transport (HTTP/2 over TLS via libcurl) vs. a custom framing layer beneath it
  — resolve in M2.
- Whether the `:6112` protocol is fully custom or wraps a known serialization — resolve in M2.
- Persistence format (SQLite vs. embedded store) — decide at M5 planning.
