# M2 — Protocol hypotheses (login + gateway)

Status: **hypotheses**, each paired with the experiment that confirms or refutes it.
Evidence base: `Agents/docs/re/001-client-endpoints.md` and `Client/logs/*.txt`.

## 1. Patch / CDN phase

Observed (client log):
- `Send patching protocol version 7`
- `Web root: https://patch.Gamecdn.com/3.28.0.8/`
- `Requesting root contents`, `Requesting folder Bundles2`, `Got file list for "…"`, `Queue file to download: …`, `gateway_list.txt`

**H1.1** The patch protocol is a GGG-custom HTTP/2 API over TLS enabling: (a) get root contents, (b) get folder listings, (c) fetch files by relative path. Files are addressed by relative path under the web root.
- Confirm: point `GGG_BASE_WEB_CDN_URL` at a local HTTPS/CDN stub and record the request sequence + response schema the client expects (status codes, content type, index format).
- Refute if: the client requires an authenticated/signed response or a non-HTTP transport.

**H1.2** `gateway_list.txt` is fetched from the web root and parsed by the client to populate the gateway combobox.
- Confirm: serve a local `gateway_list.txt` and observe parsing; capture the exact bytes the real client would receive by proxying a live patch session.
- Refute if: the list is embedded/signed elsewhere.

## 2. Login / auth phase

Observed (client log):
- `Async connecting to ams.login.Game.com:20484` → `Connected … in Nms`
- `Got Instance Details from login server`
- `Connecting to instance server at <ip>:6112`

`production_login.credentials` exists (token persistence).

**H2.1** Login uses TLS (HTTP/2 via libcurl) to `{realm}.login.Game.com` on the configured login port. Realm and port come from configuration (`GGG_LOGIN_SERVER_PORT`), host from realm.
- Confirm: capture the TLS handshake + HTTP/2 stream for a login attempt (live or via `GGG_LOGIN_SERVER_PORT` + host redirection); enumerate the request paths and JSON/binary bodies.
- Refute if: the transport is a custom framing beneath TLS (the `libcurl`/HTTP2 strings argue against this).

**H2.2** Authentication is OAuth/token based (credentials file), producing a session token later presented to the gateway.
- Confirm: inspect the credential file schema (without exfiltrating the token), and the requests in a captured login.
- Refute if: a challenge/response keyed on the client binary (would require binary patching the login client).

**H2.3** The login server allocates an instance and returns "Instance Details" containing the instance server `ip:port` and a session ticket.
- Confirm: capture the "Instance Details" response and match its fields against the client's `Connecting to instance server at <ip>:6112`.
- Refute if: the IP/port comes from a separate directory service.

## 3. Gateway / instance phase (`:6112`)

Observed: raw TCP to `<ip>:6112`; log `[JOB]`, entity/zone activity thereafter.

**H3.1** `:6112` is a custom length-prefixed, likely obfuscated/encrypted binary protocol, with an initial handshake carrying the ticket from the login server.
- Confirm: capture the first bytes of a live instance connection and compare across sessions (repeating structure, opcode fields, checksums).
- Refute if: the channel is TLS or a known serialization (protobuf/flatbuffers) — check entropy and framing.

**H3.2** Opcodes are grouped by subsystem (zone enter, entity, chat, inventory, combat).
- Confirm: correlate captured frames with client log events during scripted in-game actions.
- Refute if: a single multiplexed stream with sub-channels.

## 4. Redirection strategy (feeds M3)

**H4.1** `GGG_*` environment variables can redirect the patch/CDN/website and the login port without patching the binary.
- Confirm: launch the client with `GGG_BASE_WEB_CDN_URL` / `GGG_LOGIN_SERVER_PORT` pointing at local servers and observe connection attempts.
- Refute if: values are validated/signed or overridden by the profile file.

**H4.2** The login **host** for the GGG profile is not env-overridable and must be redirected via DNS/hosts or a binary patch.
- Confirm: trace hostname resolution/synthesis in the client (REA/Ghidra) around the `.Game.com` string; test if `HOTCOOL_LOGIN_SERVER`-style override applies to the GGG profile.
- Refute if: a GGG profile host key or a base-URL-derived host exists.

## 5. Immediate next probes

1. Extract and inspect the configuration profile (`GGG_Game_1`) — locate where profile values live (bundled vs external) to learn the full override set (notably any `GGG_LOGIN_SERVER` host key).
2. Capture a live patch + login + instance session (network capture) to convert H1–H3 from hypotheses to observations.
3. REA/Ghidra: xref the `GGG_*` strings and the `.Game.com` host builder to enumerate the exact config lookup and login-host synthesis.
