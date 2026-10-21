# M2 — Client endpoint & configuration evidence

- **Target:** `Client/Game.exe` (Path of Exile `3.28.0f`, patch `3.28.0.8`, Windows x64)
- **Method:** raw string extraction via `tools/GameClientRecon` (ASCII + UTF-16LE) over `Game.exe`.
- **Offsets** below are file offsets in `Game.exe` (`strings` output columns `offset<TAB>encoding<TAB>value`).

## Hostnames / URLs

| Offset | Enc | Value |
| --- | --- | --- |
| `02E34800` | ascii | `patch.Game.com` |
| `02E34818` | ascii | `patch2.Game.com` |
| `02E34830` | ascii | `patch3.Game.com` |
| `02E34848` | ascii | `patch4.Game.com` |
| `02E34880` | ascii | `patch.Game.tw` |
| `02E348D0` | ascii | `patch-kr.Game.com` |
| `02FD9538` | utf16 | `login.Game.tw` |
| `02FD9568` | utf16 | `login2.Game.tw` |
| `02FD9598` | utf16 | `login3.Game.tw` |
| `03393618` | utf16 | `.Game.com` |
| `02FD96C0` | utf16 | `www.Game.com` |
| `02FD97B0` | utf16 | `web.Gamecdn.com` |
| `02E41720` | utf16 | `webtw.Gamecdn.com` |
| `02FD98F8` | utf16 | `Game.game.daum.net` (Kakao) |
| `02FD98A8` | ascii | `http://kakao-pauth-api.Game.com` |
| `02FD98D0` | ascii | `http://pcbrestapi.Game.com` |

The login host is **realm-qualified** (`{realm}.login.Game.com`); only the regional `.tw` variants appear verbatim. The generic `.Game.com` suffix confirms the host is assembled at runtime (the client log shows `ams.login.Game.com:20484`, `par.login.Game.com:20493`).

## Port candidates

| Offset | Enc | Value |
| --- | --- | --- |
| `02FD9668` | ascii | `20481` |
| `02FD9784` | ascii | `20489` |

No literal `20484`/`20493`/`6112` strings exist; the live login port (`20484`) is provided by configuration, and the instance port `6112` is supplied by the login server in the "Instance Details" response (per `Client.txt`).

## Server-configuration override keys (environment variables / config profile)

The client reads a named configuration profile (`CONFIGURATION_NAME`, `GGG_Game_1`) whose values can be overridden by environment variables. Keys found:

| Key | Purpose (inferred) |
| --- | --- |
| `GGG_BASE_WEBSITE_URL` | Account website base (`www.Game.com`) |
| `GGG_BASE_WEB_CDN_URL` | **Patch CDN base** (`patch.Gamecdn.com`) |
| `GGG_REMOTE_SHADER_COMPILER_URL` | Remote shader compiler endpoint |
| `GGG_MINIDMP_UPLOAD_URL` | Crash-dump upload endpoint |
| `GGG_LOGIN_SERVER_PORT` | **Login server port** |
| `GGG_CONFIG_FILE_NAME` / `GGG_CREDENTIAL_FILE_NAME` | Config / credential file names |
| `GGG_DISABLED_CLASSES` / `GGG_STAFF_DISABLED_CLASSES` | Feature gating |
| `CONFIGURATION_NAME` / `CONFIGURATION_DISPLAY_NAME` | Profile selection |
| `HOTCOOL_LOGIN_SERVER` | **Login server host** (CN region) |
| `HOTCOOL_LOGIN_SERVER_PORT` / `HOTCOOL_BASE_WEBSITE_URL` | CN region overrides |
| `KAKAO_BASE_URL` / `KAKAO_AUTH_API_URL` / `KAKAO_PCBANG_API_URL` | KR region |
| `EPIC_DEPLOYMENT_ID` / `EPIC_CLIENT_ID` / `EPIC_CLIENT_SECRET` / `EPIC_PRODUCT_ID` / `EPIC_SANDBOX_ID` | Epic |
| `STEAM_APP_ID` / `HOTCOOL_STEAM_APP_ID` | Steam |

**Significance:** the patch/CDN/website/shader endpoints and the login **port** are overridable without touching the binary. A login-server **host** override (`HOTCOOL_LOGIN_SERVER`) exists for the CN profile, implying the config schema can express a login host; whether a GGG-profile equivalent exists is unconfirmed.

## Local client state (read-only inspection)

`%USERPROFILE%\Documents\My Games\Path of Exile\`:

- `production_Config.ini` — `[LOGIN]` section:
  - `gateway_auto_select=false`
  - `gateway_id=Europe`
  - `save_login=true`, `username=…`, `account_name=…`
  - `[NETWORKING] networking_type=predictive`
- `production_login.credentials` — persisted login token (contents not reproduced).
- `release/` and `OnlineFilters/` directories.

## UI / diagnostic strings

| Offset | Enc | Value |
| --- | --- | --- |
| `02FD9538`* | utf16 | (see hostnames) |
| `02FD0D30` | utf16 | `Got Instance Details from login server` |
| — | — | `The login server port` |
| `02FCC148` | utf16 | `gateway_list.txt` |
| `02FCC170` | utf16 | `Bundles2/gateway_list.txt` |
| `02FCBC38` | utf16 | `gateway_combobox` |
| `0326EC58` | utf16 | `gateway_id` |
| `0326EC70` | utf16 | `gateway_auto_select` |
| `02FCCCD0` | ascii | `path_of_exile_gateway_ping` |
| `02F51D4C` | ascii | `realm` / `&realm=` / `?realm=` |

`gateway_list.txt` is obtained from the patch CDN (root), not the GGPK; it is not present at `Bundles2/gateway_list.txt` inside our archive.

## The shop and the Cosmetics panel (2026-10-21)

The in-game cosmetics UI is native — RTTI strings `UI::Controls::InGame::CosmeticsPage`,
`MicrotransactionListPanel`, `MicrotransactionCategoryBar`, `MicrotransactionOption` and
`AddItem(MicrotransactionOption, PurchasedMicrotransaction, bool)` — and it is **built from purchased
microtransactions**, not from a game-server message:

- The web endpoints in the binary (UTF-16): `/api/shop-microtransaction-categories?forsale=1`,
  `/api/shop/microtransactions/specials?type=active&limit=5&platform=`, `/api/client-data`, `/api/settings`,
  `/api/client-item-filter`, `/api/tracking-conversions/`. The tracking host is Tencent's `pub.game.daum.net`.
- Live (`scripts/re/frida/trace-http.js` attached at launch with `run.py --follow`, 2026-10-21): the client resolves and
  opens TLS only to `web.poecdn.com`; the patch/web root is our own `http://127.0.0.1:8080/`. Opening the shop and its
  cosmetics section made **no** request at all, and the list stayed empty — the catalogue/ownership it needs is fetched
  from GGG's web APIs, which a local account has no session for.
- The login protocol has no microtransaction message (`01-login.md` §4: 0x03/0x18/0x12/0x19/0x0e only), so there is no
  server-side list to fill either.

Consequence: a picker cannot be the client's own cosmetics page without intercepting `web.poecdn.com` TLS (certificate
bypass) and faking an account's purchases. Server-authored lists exist only where rows are items; the vendor window's
**tab names** are the one server-written list of arbitrary text (`17-npcs.md` §5).

## Consequences for the private server

1. **Patch/CDN and login port are redirectable via `GGG_*` env vars** — no binary patch needed for those.
2. **Gateway selection** is governed by `production_Config.ini:[LOGIN].gateway_id` plus the CDN `gateway_list.txt`; we control both.
3. **Login host** for the GGG profile is assembled from realm + `.Game.com`; redirection likely needs either a host override we have not yet confirmed, DNS/hosts redirection, or a targeted binary patch.
4. `production_login.credentials` implies a token-based login flow that our auth server must emulate.
