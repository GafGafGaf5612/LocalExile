# Native client control

`scripts/dev/client_control.py` provides CLI and persistent JSON-lines RPC for the copied standalone client
(3.29.3b today; it finds the client's code by signature, so a patched client keeps working where it can, see
[below](#how-the-bridge-finds-the-clients-code)). It calls existing client functions through [Frida RPC](https://frida.re/docs/javascript-api/#rpc), on the
game thread. It uses the normal local authentication, character list, Play, instance connection, and Gateway command
handlers. No screenshots, keyboard/mouse events, HTTP listener, save editing, or server authentication bypass are used.

Start the local servers normally through `scripts/dev/live.ps1`. Python needs `frida` (the walk-map recorder's `.venv`
already has it). Run from the repository root:

```powershell
.\scripts\dev\control.ps1 game:open
.\scripts\dev\control.ps1 login:offline         # private password prompt; use the local account's existing password
.\scripts\dev\control.ps1 character:list
.\scripts\dev\control.ps1 character:play:OfflineWitch
.\scripts\dev\control.ps1 game:status
.\scripts\dev\control.ps1 command '/hit physical 100'
.\scripts\dev\control.ps1 chat '#hello'           # any chat line: local, #global, $trade, or a slash command
.\scripts\dev\control.ps1 tp 'RhoaChest 2'        # /tp: also tp '120 340' (a cell) or tp '+10 -5' (relative)
.\scripts\dev\control.ps1 character:select
.\scripts\dev\control.ps1 logout
.\scripts\dev\control.ps1 bridge:doctor       # how every client address was found; -ForceScan skips the pinned ones
```

`game:open` attaches to an existing client, or calls `live.ps1 -ClientOnly`. That switch starts only the copied client
with the local patching-server argument and requires the local servers already running; it does not restart services,
rebuild, extract data, or close an existing client. It waits up to 60 seconds for a native menu, so login can immediately
follow its successful result. Before opening a closed client, it resets the cached GGPK gateway list to local Auth,
as a normal startup does; an existing client returns before that write. Startup asset loading still takes the
client's normal time. After merging upstream's gateway reset, a fresh client opened against the already-running
Patch/Auth/Gateway services in 9.5 seconds, then logged in, listed characters and entered an area successfully
(2026-10-25, isolated validation database). Mocked startup checks also cover existing clients and failed preflights.

Every command returns one JSON object: `ok`, `result` or `error`, and `elapsedMs`. `character:list` reads the list
actually received by the client; entries have `index`, `name`, `league`, `level`, and `classId`. Play resolves the name
against that list and waits for the client to enter the area. Replaying the active character and opening an already
running game are idempotent. Login reauthenticates, returning to selection first if necessary. A wrong password returns
`ok:false` with login result 6; retrying works without restarting. `command` sends a slash command, `chat` any chat
line (the client's own 0x15, as typed in the chat box), and `tp` the `/tp` dev command built from `x`/`y`, `dx`/`dy`,
`id`, `target` (+ `index`) or `list`. For slash commands the result also has `replies`: the chat lines the Gateway
answered with, read from `logs/live-gateway.log` (its `=> 0x0017 chat line` entries) until 0.25 s pass without a new
one or `wait` seconds (default 1.5) in total. `replies` is null when the servers were not started by this checkout's
`live.ps1` (no log); a command without an answer (e.g. `/goto`) returns an empty list after `wait`. Replies of commands
sent from the chat box meanwhile are mixed in; the bridge does not capture the HUD.

## Persistent integration

Spawn `.venv/Scripts/python.exe scripts/dev/client_control.py --stdio` with stdin/stdout pipes. Write one JSON object
per line, flush stdin, and read one JSON result line. This retains the Frida attachment between calls:

```json
{"command":"login","account":"offline","password":"your-local-password"}
{"command":"character:list"}
{"command":"character:play","name":"OfflineWitch"}
{"command":"command","text":"/npc Tarkleigh"}
{"command":"command","text":"/pos","wait":0.5}
{"command":"tp","target":"RhoaChest","index":2}
{"command":"tp","x":120,"y":340}
{"command":"chat","text":"#hello"}
{"command":"game:status"}
```

These lines illustrate the format; consume each result before submitting the next command. Credentials are neither
echoed nor persisted. CLI login also accepts `--password-stdin` for a password supplied through a pipe. Do not put real
credentials in tracked scripts or shell arguments. The Python `ClientControl` context manager exposes `execute(request)`
for scripts in the same process. Closing stdin or leaving the context detaches; the game keeps running.

## Client implementation and verification

The bridge attaches only to this checkout's `Client/Game.patched.exe` (checked by path before anything is resolved).
It calls and hooks these client functions; the names are the symbols in `scripts/dev/client-signatures.json`:

| Symbol | Purpose |
|---|---|
| `frame` | Hook inside the state-stack update, after its `+0x38` transition guard is entered. Calling transitions before this guard can destroy live UI state. One pending RPC is serviced here, with a five-second cancellation if no frame arrives. Every command needs it. |
| `app` / `stack` (globals) | App and state-stack singleton pointers. App `+login` (`app.login`) is the login object, `+instance` the game connection. |
| `connect` | Connect using UTF-16 host/port strings; bridge forces `127.0.0.1`. |
| `loginWriter` | Native account-login writer. The handshake listener can auto-submit its saved form before a second RPC arrives; supply the requested account/digest to this **one** submission. The native string assignment (`assignText`) owns its allocation; the digest is cleared from bridge memory after copying. |
| `login` | Login sender, used only if the handshake listener has not already submitted. |
| `dispatch` | Capture the actual login result (`0x04`, result at `+0x10`). |
| `login.characters` (offset) | Received character vector, `character.stride` (0x50) byte entries. |
| `play` | Play with the chosen name view and list index. Set the in-game state's `ingame.classId` to its class; push **state 11 (loading)** with `push`, which installs the assignment listener. Directly pushing state 4 skips that listener. |
| `logout` | Existing root login state's logout routine; preserves the correct login form lifecycle. |
| `write` / `flush` | Existing buffered socket writer/flush, for `0x153` return to selection, `0x15` chat and interaction messages. Encryption remains the client's. |
| `lifeTick` / `poolUpdate` | Read-only hooks for `game:resources`, attached on its first use. |

The other symbols (`stackGetter`, `appGetter`, `activeSend`, `loginSend`, `characterList`, ...) are anchors: code the
bridge never calls, but whose instructions name a global or a struct offset, or call one of the functions above.

### How the bridge finds the client's code

Everything the bridge knows about the client is data in `scripts/dev/client-signatures.json`, no addresses are in
the JavaScript. `client_control.py` passes the file to the agent (`scripts/dev/client-resolver.js`, loaded before
`client-control.js`), which resolves each symbol at attach, in this order:

1. **pinned**: the symbol's image VA (`va`), if the bytes there still equal `bytes` (the whole signature length, not
   just a prologue). This is the normal case and costs about 2 ms.
2. **signature**: the wildcarded `pattern` (hex bytes; `??` for call/jump displacements, RIP-relative operands and
   stack-frame sizes, and for every captured operand) occurs **exactly once** in the executable's code
   (`Memory.scanSync`). A symbol with `offset` is that many bytes into its match (the `frame` hook is 0x7f bytes into
   the state-stack update, whose signature starts at the function). `start: function` requires the match to begin a
   function in the executable's `.pdata` table; `start: inside:<symbol>` searches only the 16 KiB after that symbol
   and requires the match to belong to the same function (following chained unwind entries).
3. **xref** / **string**: the call target captured in a resolved caller (`callers`, e.g. `write` is the callee of
   `loginWriter`), or the one function that references a unique NUL-terminated `string` with `lea reg, [rip+X]`.
   The signature must still match there; if not, the symbol resolves but its captures are unavailable and doctor
   says so.
4. otherwise **unresolved**.

`captures` name operands inside a signature: `rel32` (a call target), `rip` (a RIP-relative address: the globals),
`disp` (a struct displacement) and `imm` (an immediate), with their byte position. Each value is read from the bytes
and cross-checked against the instruction decoded at `insn` by Frida's `Instruction.parse`. Globals come only from
their capture (a moved function means moved data, so a pinned global is never trusted alone). Offsets come from
every listed capture (`from`; they must agree, e.g. `login.socket` appears in three functions) plus `add`, or fall
back to the pinned `value` when no source resolved (doctor says `constant`, `sources unresolved`). Eight offsets have
no instruction in the anchored code that uses them and are always constants: the character entry stride, league and
class id, `state.id`, `ingame.classId`, `pool.fraction`, `update.pool` and `message.loginResult`.

Each operation declares what it needs (`NEEDS` in `client-control.js`: every operation needs `frame` and what
`status` reads). If something is missing, that operation fails with
`Unsupported client: play needs play (not found)` and the others keep working; hooks attach only when found. A
missing `frame` makes every command fail and is printed as a warning at attach. Before trusting pointers the bridge
also checks the layout cheaply: object pointers non-null, aligned and readable, login and instance states 0-3, state
ids below the stack's state count, string lengths and capacities, character levels 1-100 and class ids, the in-game
class field before it writes it. A wrong offset then gives `Client layout check failed: login.state = 912
(expected 0-3)` instead of a crash.

Resolving everything without the pinned addresses takes about 0.6 s in the client (0.2 s offline), so there is no
cache.

### bridge:doctor and the force-scan switch

`.\scripts\dev\control.ps1 bridge:doctor` (JSON RPC `{"command":"bridge:doctor"}`) returns, per symbol, global and
offset, the method (`pinned`, `signature`, `xref`, `string`, `constant` or `unresolved`), the image VA or value and
the pinned one, and why something failed; the hooks; whether each operation is available; sanity checks of the live
state; and the executables: the build label the client reports for itself (`3.29.3b`, from its `/tags/` string;
the executable has no version resource), the SHA-256 of `Game.patched.exe` and of the unpatched `Game.exe`, and
whether the latter is the build the signatures were made for (`client.sha256`, the official `PathOfExile.exe` of
patch version `client.version`). `game:status` reports the same `build` and `patchVersion`.

`--force-scan` (`control.ps1 -ForceScan`, or `LOCALEXILE_CONTROL_FORCE_SCAN=1`) skips the pinned addresses, so every symbol must be found by
its signature; `--force-scan xref` (`LOCALEXILE_CONTROL_FORCE_SCAN=xref`) also prefers cross-references over
signatures. On 3.29.3b both resolve every symbol, global and offset to exactly the pinned values.

### Adding or fixing a symbol

1. Find the function (and, for an offset or global, an instruction that uses it) in `Client/Game.exe`.
2. Draft the entry: `.venv\Scripts\python.exe -I scripts/re/signature_pattern.py <start VA> <end VA or +length>
   --capture <instruction VA>:<name>[:disp|imm|rel32]`. It prints the disassembly, the pattern with the moving bytes
   wildcarded, how often it occurs, and the JSON (`va`, `pattern`, `bytes`, `captures`). Extend the range until it
   occurs once; prefer distinctive instruction sequences to prologues (a 16-byte prologue matched up to 3964
   places). Run it with `-I`: `scripts/re/dis.py` would otherwise shadow Python's `dis` module.
3. Add `start`, `offset` (for a hook inside a function), `callers` and `string` where they apply, and the offsets'
   or globals' `from`. Name the needs of the operation in `NEEDS`.
4. Check: `python -I scripts/ci/resolve_client.py Client/Game.patched.exe --check` (and `--mode xref`), then
   `.venv\Scripts\python.exe scripts/dev/check_client_signatures.py`, the unit tests, and live with `bridge:doctor`
   and `--force-scan`.

### When the client is patched

`scripts/ci/resolve_client.py` (standard library, any OS) applies the same rules to an executable on disk: it reads
it in place (mmap; a pattern is searched by its longest literal run, then verified), recomputes every VA, expected
bytes, global and offset, and with `--write --version <v>` stores them and the new SHA-256 and build label in the
JSON. `--report` writes a markdown table (symbol, old VA, new VA, method, status). Exit 0: nothing changed; 1:
updated and everything resolved; 2: something is unresolved (the rest is still written). `--check` instead requires
every pinned value to be reproduced (by signature, or `--mode xref`).

`.github/workflows/client-patch-watch.yml` runs it when Grinding Gear Games ships a new client. It is opt-in: the
schedule (every 30 minutes) does nothing unless the repository variable `CLIENT_PATCH_WATCH` is `true`, and the
repository must allow Actions to create pull requests; a manual run (with `force`) always works. Two jobs:

- **poll** (2-4 s, no checkout or setup actions): fetches `scripts/ci/patch_version.py` and the JSON through the API,
  asks the patch server (`patch.pathofexile.com:12995`, request `01 07`) for the current CDN web root
  (`https://patch.poecdn.com/<version>/`) and compares the version with `client.version`. A new version without a
  `client-patch/<version>` branch starts the next job.
- **resolve** (15-18 s): downloads `<web root>PathOfExile.exe` (79 MB) to the runner's temporary directory,
  requires a valid Grinding Gear Games Authenticode signature (osslsigncode, unpacked from its package instead of
  installed; the signer itself must be Grinding Gear Games; fail closed), runs `resolve_client.py --write`, the
  `scripts/ci` tests (with everything resolved, also the simulated-patch tests on the new client) and `node --check`,
  deletes the client and opens or updates the pull request `client-patch/<version>` (a draft labelled
  `needs-live-check` when something is unresolved). The client is never committed or uploaded. The pull request
  says that CI cannot run the game: someone must run `bridge:doctor` (also with `--force-scan`) and the flow
  above with the new client before merging.

The CDN ignores HTTP `Range` (it answers 200 with the whole file), and the Authenticode hash covers the whole file
anyway, so the job downloads it once. Measured on GitHub's runner: download 4-5 s, verification 1-2 s, resolving
0.12 s when pinned (24 MB peak RSS) and 0.2 s when every signature is scanned (72 MB, mostly the 50 MB code
section mapped from the page cache).

### Checks

- `node --check scripts/dev/client-resolver.js` and `node --check scripts/dev/client-control.js`;
  `.venv/Scripts/python.exe -m unittest discover -s scripts/dev -p test_client_control.py`;
  `python -I -m unittest discover -s scripts/ci -p "test_*.py"`: the resolver on small synthetic PE images (moved
  code, changed RIP-relative targets and struct offsets, an ambiguous signature refused, a callee found through its
  caller, a rewritten function found by its string, the function-start check, chained unwind entries), the
  consistency of `client-signatures.json`, and, when `Client/Game.exe` exists, the real client with its whole code
  section moved and the captured struct offsets changed.
- `python -I scripts/ci/resolve_client.py Client/Game.patched.exe --check` (`--mode xref`; also `Client/Game.exe`):
  every signature occurs once, at its pinned VA, without starting the game.
- `.venv/Scripts/python.exe scripts/dev/check_client_signatures.py`: maps `Game.patched.exe` into a helper process
  without running any of it (`LoadLibraryEx` with `DONT_RESOLVE_DLL_REFERENCES`), runs the bridge's own
  `client-resolver.js` there in all three modes, and compares it with the offline resolver.
- `powershell -NoProfile -File scripts/dev/test_live_client_only.ps1` checks closed/existing client startup,
  local gateway selection, and failed preflights with mocked processes and no GGPK writes.

Live verified 2026-10-25: fresh `game:open`, login, actual level-100 OfflineWitch list, Play, return to selection, logout,
wrong-password rejection, successful retry, Play again, and `/hit physical 1` received by the Gateway. In one persistent
run: login 0.7–0.9 s, list 94 ms, Play 1.4–2.2 s, command submission 94 ms; timings include game frames and loading,
and are observations rather than guarantees. Only the Witch class was exercised live.
`tp` checked live 2026-10-25 in The Mud Flats (seed 1): after `/tp RhoaChest 2`, `/tp GlyphWall` and `/tp +10 +0` the
client's own cell (Positioned +0x294/+0x298, as `scripts/re/frida/trace-player-drift.js` reads it) equalled the
Gateway's `/pos` every time, and the `/pos`, `/tp list` and refusal replies came back in `replies`. An attached bridge is for the
local development client; it has no official-service support.
Signature resolution checked live 2026-10-25 with the same flow (login, list, Play, status, resources, `/pos` with
`replies`, chat, `tp`, `flask:use 1`, selection, logout) three times: pinned, `--force-scan` and `--force-scan xref`,
with identical results and timings (login 0.6-0.8 s, list 0.1 s, Play 1.5 s). With one symbol's signature broken on
purpose, only its operation failed (`Unsupported client: play needs play (not found)`); with `frame` broken every
command failed with that message and the attach warning, and `bridge:doctor` still answered.

### Limits

Only one client build exists to test against, so surviving a real patch cannot be proven here: the moved-code and
changed-offset tests simulate what a patch usually does, but a patch can also change the compiled code itself
(different registers, inlining or instruction order), which breaks a signature; the cross-references and string
anchors then often still find the function, and doctor and the CI report name what is missing. Constant offsets are
not re-derived, and a changed struct layout behind them is caught only by the sanity checks. `Game.patched.exe`
differs from the official executable only in the login patch, far from every signature, so the signatures match
both. The server may need its own changes for a new client.

The other Frida scripts under `scripts/re/frida/` (traces and probes, not loaded by `client_control.py`) still use
pinned 3.29.3b addresses; to make one patch-resistant, load `client-resolver.js` before it and resolve its symbols
the same way.


## Targeted development interactions

JSON RPC also accepts `game:resources` (read-only Life component pools and recent 0x1b1 corrections),
`object:interact` with an object `id`, `move` with grid `x`/`y`, `npc:option` with row `index`, `npc:close`,
`reward:claim` with choice `id` and main-inventory `x`/`y`, and `inventory:pickup`/`inventory:place` with `inventoryId`
and item `id` or destination `x`/`y`, and `flask:use` with a flask-bar `slot` (0-4) or `key` (1-5; CLI
`flask:use 1`). `flask:use` sends the flask key's own Câ†’S 0x70 (u32 cell, [13](../protocol/13-items.md) Â§8), so a flask
is used without keyboard input or window focus. These use the existing C?S interaction messages. Their result acknowledges
submission; wait for the resulting state or logs. Movement uses the client's Move action (0x2909). Interaction from
far away can fail to find a path; `tp` next to the object first (`{"command":"tp","target":"GlyphWall"}`), then interact. Resource updates are bounded to 128 entries and drained on
read, so persistent RPC is needed to observe a recovery interval. This measures client state, not HUD pixels.

Live interaction checks included Tarkleigh's reward claim, starter weapon pickup/equip, the biting zombie's death and
gem pickup, walking to the support chest and opening it. The client stayed attached through these tests. RPC completion
is deferred until its native callback has returned, so a one-shot process can detach without unloading a hook while
it is still unwinding. Prefer loading additional probes into the same Frida session when extending this tool.
