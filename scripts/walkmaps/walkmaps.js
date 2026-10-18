// Walk-map capture (Frida, loaded by walkmaps.py into the running client).
//
// When the client generates an area it builds a terrain walkability grid before drawing it. Right after the grid
// provider returns (client 3.29.3b, VA 0x1410ddcfd, just after the vtable +0x50 call in 0x1410ddb90) rax points to
//   { i32 width; i32 height; vector<u8> cells [+0x08 begin, +0x10 end]; vector<u8> second [+0x20, +0x28] }.
// The first vector is width × height bytes, one per cell; the client walks on cells >= 2. The second, the same size,
// stops projectiles where it is 0 (walls, doodads; water is open there).
// Each grid is sent to walkmaps.py, which names it after the area the Gateway is serving.

const IMAGE_BASE = ptr('0x140000000');
const HOOK_VA = ptr('0x1410ddcfd');
// The instructions at the hook in the 3.29.3b client (mov rcx,[rax]; mov [rbp-0x38],rcx; lea rdx,[rax+8]; ...).
// Nothing is hooked unless they match, so another client build is left untouched.
const EXPECTED = '488b0848894dc8488d5008488d4dd048';

// The client executable: the .exe module (Process.mainModule can report a driver DLL when attaching late).
const exe = Process.enumerateModules().find(m => m.name.toLowerCase().endsWith('.exe'));
if (!exe) {
  send({ type: 'refused', reason: 'no .exe module in the process' });
} else {
  const hook = exe.base.add(HOOK_VA.sub(IMAGE_BASE));
  const inside = hook.compare(exe.base) >= 0 && hook.add(EXPECTED.length / 2).compare(exe.base.add(exe.size)) <= 0;
  const bytes = inside ? Array.from(new Uint8Array(hook.readByteArray(EXPECTED.length / 2)))
    .map(b => b.toString(16).padStart(2, '0')).join('') : '';
  if (bytes !== EXPECTED) {
    send({ type: 'refused', reason: `${exe.name} is not the 3.29.3b client (code at the hook does not match)` });
  } else {
    Interceptor.attach(hook, function () {
      try {
        const r = this.context.rax;
        const w = r.readS32(), h = r.add(4).readS32();
        const begin = r.add(0x08).readPointer(), end = r.add(0x10).readPointer();
        const n = end.sub(begin).toInt32();
        if (w <= 0 || h <= 0 || n !== w * h) {
          send({ type: 'skip', w, h, bytes: n });
          return;
        }
        send({ type: 'grid', w, h }, begin.readByteArray(n));
        // The second vector, same size: what stops projectiles (0 blocks; water and the like are 1..5 there).
        const b2 = r.add(0x20).readPointer(), n2 = r.add(0x28).readPointer().sub(b2).toInt32();
        if (n2 === w * h) send({ type: 'projectile', w, h }, b2.readByteArray(n2));
      } catch (e) {
        send({ type: 'error', message: String(e) });
      }
    });
    hookTileObjects(exe);
    send({ type: 'ready', module: exe.name });
  }
}

// Area transitions and waypoints of the generated layout (Agents/docs/protocol/08-area-transitions.md §5).
// While generating, 0x141d7f700 walks each placed tile's .tmd records and calls 0x1420415e0(ctx, record):
//   ctx[1] → placed tile: +0x30 i16 height offset (steps of 7.8125), +0x36 u8 rotation/flip code 0..7
//   ctx[2] → i32 x, i32 y: the tile's grid origin;  ctx[3] → handle, *handle+0x28 → +0x60/+0x61 u8 size in tiles
//   record: i32 x, i32 y (tile-local cell), f32 z (+0x10), f32 angle (+0x14); +0x30 type, +0x38 .ao (handle+8 → path)
// The client spawns only its own doodads from them; server types (AreaTransition …) are skipped there, so their
// placement is computed here the way 0x14202f1c0 does it, with the constants read from the client.
function hookTileObjects(exe) {
  const at = va => exe.base.add(ptr(va).sub(IMAGE_BASE));
  const hook = at('0x1420415e0');
  const bytes = Array.from(new Uint8Array(hook.readByteArray(16))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (bytes !== '488bc448895808555657415441554156') {
    send({ type: 'refused', reason: 'tile object hook: code does not match 3.29.3b' });
    return;
  }
  const codes = Array.from(new Uint8Array(at('0x142fa04b0').readByteArray(24)));
  const quarter = at('0x1436a3318').readFloat(), turn = at('0x1436a3674').readFloat();
  const noHeight = at('0x1436a48d4').readFloat();
  const path = h => { try { return h.isNull() ? null : h.add(8).readPointer().readUtf16String(); } catch (e) { return null; } };
  Interceptor.attach(hook, {
    onEnter(a) {
      try {
        const ctx = a[0], rec = a[1];
        const type = path(rec.add(0x30).readPointer());
        if (!type || !/AreaTransition|Waypoint$/.test(type)) return;
        const tile = ctx.add(8).readPointer();
        const code = Math.min(tile.add(0x36).readU8(), 8), heightOffset = tile.add(0x30).readS16();
        const origin = ctx.add(0x10).readPointer();
        const info = ctx.add(0x18).readPointer().readPointer().add(0x28).readPointer();
        const w = info.add(0x60).readU8(), h = info.add(0x61).readU8();
        const x = rec.readS32(), y = rec.add(4).readS32();
        const c = code < 8 ? codes.slice(code * 3, code * 3 + 3) : [0, 1, 1];
        const cells = [w * 23 - x - 1, x, h * 23 - y - 1, y];
        const gx = cells[c[1] + 2 * c[0]] + origin.readS32(), gy = cells[(c[0] === 0 ? 2 : 0) + c[2]] + origin.add(4).readS32();
        let rotation = rec.add(0x14).readFloat();
        if (code >= 4 && code < 8) rotation = turn - rotation;
        rotation += (code & 3) * quarter;
        while (rotation < 0) rotation += turn;
        while (rotation > turn) rotation -= turn;
        const z = rec.add(0x10).readFloat();
        send({ type: 'object', objectType: type, model: path(rec.add(0x38).readPointer()), x: gx, y: gy, rotation,
               z: z === noHeight ? null : z - heightOffset * 7.8125, rawZ: z, heightOffset, code });
      } catch (e) {
        send({ type: 'error', message: 'tile object: ' + String(e) });
      }
    },
  });
}
