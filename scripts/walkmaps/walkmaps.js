// Walk-map capture (Frida, loaded by walkmaps.py into the running client).
//
// When the client generates an area it builds a terrain walkability grid before drawing it. Right after the grid
// provider returns (client 3.29.3b, VA 0x1410ddcfd, just after the vtable +0x50 call in 0x1410ddb90) rax points to
//   { i32 width; i32 height; vector<u8> cells [+0x08 begin, +0x10 end]; vector<u8> second [+0x20, +0x28] }.
// The first vector is width × height bytes, one per cell; the client walks on cells >= 2.
// Each grid is sent to walkmaps.py, which names it after the area the Gateway is serving.

const IMAGE_BASE = ptr('0x140000000');
const HOOK_VA = ptr('0x1410ddcfd');
const main = Process.mainModule;
const hook = main.base.add(HOOK_VA.sub(IMAGE_BASE));

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
  } catch (e) {
    send({ type: 'error', message: String(e) });
  }
});

send({ type: 'ready', module: main.name });
