# Patch protocol captured + client-state recovery

## Patch (CDN) protocol — observed

With DNS redirect in place (`patch.Gamecdn.com` → 127.0.0.1) and our dev cert
trusted machine-wide, the client's patch phase was fully observed:

- **Transport:** HTTPS, plain per-file GETs (cURL backend, HTTP/2 capable).
- **Request shape:**
  ```
  GET /{version}/{path}
  Authorization: Basic <redacted>   # the official CDN credential, not reproduced
  Accept: */*            Accept-Encoding: gzip
  ```
- **Examples:** `/3.29.3.3/Bundles2/Tiny_5.bundle.bin`,
  `/3.29.3.3/Bundles2/Folders/.../foo.bundle.bin`,
  `/3.29.3.3/ShaderCacheD3D11.packed/84.dat`, `/3.29.3.3/FMOD/Desktop/*.bank`.
- Individual `.bundle.bin` / `.dat` / `.bank` files are fetched directly by path;
  the manifest appears to be the client's own cached hash state
  (`Client/HashCache.dat`) plus the bundle index, not a fetched "root contents" file
  (no root/manifest request was observed reaching our server).
- All requested logical paths **exist as physical files in the local GGPK**, so our
  Patch server served them straight from `Content.ggpk` (see `src/GameServer.Patch`).

Our **Patch server now serves the client its own files back and overrides
`gateway_list.txt`**, which controls the login host:port (`Agents/docs/re/004`).

## The wall: client/data version mismatch

- The installed executable **self-updated 3.28.0f → 3.29.3b** when first launched
  (exe now 79 MB, contains `tags/3.29.3b`); the client then targets content
  `3.29.3.3`.
- The installed **data was 3.28**. Serving 3.28 bytes under `/3.29.3.3/...` produced
  `Downloaded file … has the wrong hash` and the client kept trying to update.
- **Root cause:** client and data must be the same version. We could not complete a
  real 3.29 update (large delta) within this session.

## Side effects on the client (must be repaired)

- `Game.exe`: updated to 3.29.3b (the client did this itself).
- `Content.ggpk`: grew ~63.3 GB → ~70.6 GB during the patching experiments
  (partially patched / inconsistent).
- `PackCheck.exe`: truncated to 0 bytes.
- Redirect (hosts + cert) was **removed**; `patch.Gamecdn.com` resolves publicly again.

### Recovery

1. Run the official launcher (`Game_x64.exe` / `Client.exe`). With the
   redirect removed it will hash-check and **re-download the correct 3.29 content**,
   repairing `Content.ggpk` and restoring `PackCheck.exe`. This may be a large
   download.
2. If the launcher refuses to start (PackCheck), delete `PackCheck.exe` and retry,
   or use the standalone/Steam "Verify files" option.

## Correct path to a private server (next)

1. **Pin one version.** Either let the client finish updating to 3.29.3b (then
   re-extract 3.29 data), or restore a matching 3.28 client+data pair. Everything
   downstream depends on this.
2. Re-apply the redirect (`tools/redirect/setup-redirect.ps1 -Action install`).
3. With client and data consistent, the patch phase becomes a no-op and the client
   proceeds to **login** — which our `GameServer.Auth` will then capture (request
   shapes → implement).
4. Then capture/implement the `:6112` gateway protocol.

The redirect mechanism, patch protocol, and server components are proven; only the
version pin and the live login/game capture remain.
