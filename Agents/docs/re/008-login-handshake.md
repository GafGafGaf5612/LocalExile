# 008 — Login connection key exchange and encryption (verified 2026-10-16)

Client 3.29.3b (`Client/Game.exe`, sha256 78a0e227…), image base 0x140000000. Derived statically with the Ghidra
headless project (`scripts/re/ghidra/g.sh`), cross-checked by an independent Python client against
`GameServer.Auth` and by `LoginCryptoTests` (which patch and handshake against the real client image).

## Summary
| Step | Detail |
|---|---|
| 1. C→S plaintext | msg `0x0002`: `u16 2, u16 128, client ephemeral DH public (128 B BE), u16 0` |
| 2. S→C plaintext | msg `0x0002`: `u16 2, u16 128, server ephemeral public, u16 56, DSA-SHA1 signature r‖s` |
| 3. Client checks | DSA/SHA-1 over the 128-byte server key, with the 838-byte DSA key embedded in the client |
| 4. Agreement | Crypto++ `DH2`: `Z = Z_static(384 B) ‖ Z_ephemeral(128 B)` |
| 5. KDF | `km = SHA-512(Z)` (64 bytes) |
| 6. Ciphers | Salsa20/20, key `km[0:32]`; C→S IV `km[32:40]`, S→C IV `km[48:56]`; one continuous stream per direction |

Everything after the two `0x0002` messages is encrypted. All integers are big-endian.

## Groups (ctor of the crypto object, "Rhoa", `0x140e02170`)
Hex strings in the binary are parsed into two `DL_GroupParameters` objects (p, q, g). Both p values are safe primes
(p = 2q + 1), and g generates the order-q subgroup:
- static group, 3072-bit, g = 3 (object +0xb0)
- ephemeral group, 1024-bit, g = 2 (object +0x1b0)

The values are in `src/GameServer.Core/Protocol/Login/LoginGroups.cs`. The object at +0x2b0 (vtable 0x1431f55c0) holds
both groups and uses the Crypto++ `DH2` methods: ephemeral key lengths at vtable +0x70/+0x78,
`GenerateEphemeralKeyPair` at +0x90, and `Agree(out, staticPriv, ephPriv, staticOtherPub, ephOtherPub, validate)` at +0x98.

## Embedded key blobs (.rdata, all referenced only from `0x14213e610`)
| VA | File offset | Size | Contents |
|---|---|---|---|
| 0x142fc0150 | 0x2fbf550 | 0x346 | X.509 DSA public key (p 2048, q 224), the server key verifier |
| 0x142fc04a0 | 0x2fbf8a0 | 384 | client **static private** exponent (not a subgroup element) |
| 0x142fc0620 | 0x2fbfa20 | 384 | server **static public** value (passes y^q = 1) |

The DSA key is loaded through `StringSource` into the verifier and validated at level 3 (on failure:
"General Rhoa Failure #2"). The verifier vtable's accumulator slot (`0x1431f8810`) is `0x140e04a70`, which builds a SHA-1
state, so the scheme is DSA with SHA-1. The signature is in Crypto++'s default P1363 format, `r‖s`, 28 bytes each.

## Code path
- `0x14213e610` (login connect): creates the crypto object (keys above) and the per-connection state (ephemeral
  private/public, 64-byte km buffer), then writes msg 2 field by field with `htons` and flushes it.
- `0x14213f190` (login message dispatch), `case 2` → `0x140e03630`: a `SignatureVerificationFilter` (flags 8 =
  PUT_RESULT) over `key‖signature` ("General Rhoa Failure #3" if the check fails) → `0x140e03460`: `DH2::Agree`, then
  SHA-512 into km ("General Rhoa Failure #1" if Agree fails). It then builds `0x140e03a30(km, isClient=1)` and installs
  it on the socket (`+0x1b8`).
- `0x140e03a30`: two Salsa20 objects (`0x140e04840`, rounds 20, key 32 B, IV 8 B). Object A gets IV
  `km + ((flag^1)+2)*16` and object B gets `km + (flag+2)*16`. Vtable `0x1431f65e0`: slot 1 → A, slot 2 → B.
- Socket send/flush `0x140dfb8c0` calls slot 1 (A), and socket receive `0x140dfb3d0` calls slot 2 (B), so the client
  sends with `km[32:40]` and receives with `km[48:56]`.
- Message 2 is one class used in both directions (vtable 0x1435c0038), deserialized by `0x141f9d7f0` as two
  u16-length strings (`0x1408402a0`).

## Consequence: running our own login server
We cannot sign as GGG or answer their static key, but both checks use public values embedded in the client, and
those can be swapped at the same size:
- Keep GGG's DSA domain (p, q, g) and replace only `y` with ours. We pick x until the DER is exactly 0x346 bytes again.
- Replace the server static public value with `3^s mod p` for our own s.
- The client static private key in the blob gives us the client static public value.

`GameServer.Auth keygen` / `patch` / `serve` implement this (see `Agents/docs/dev-server.md`). The patched exe differs only at
file offsets 0x2fbf796–0x2fbfb9f. Its Authenticode signature is then invalid. Integrity checks elsewhere (launcher,
PackCheck, anti-cheat) are untested.

## Still unknown
- Message framing after the handshake: messages are `u16 id` + fields with no length prefix (see the serializers
  around `0x14213bf60`), so the server needs each message's schema. Next: log the decrypted first packets from a
  patched client, then map ids through the dispatch switch in `0x14213f190` (cases 4, 0xb, 0xd, 0x13 tracking, 0x14
  area, …).
- The instance connection (`:6112`) also builds this crypto object (callers `0x1428e9a60`, `0x1400fc250`). Whether it
  uses the same blobs and key exchange is not yet checked.
- Separately, `0x140e01290` derives a one-off Salsa20 key from SHA-384 of 8 bytes. It is unrelated to the login stream.
  Its purpose is unknown.
