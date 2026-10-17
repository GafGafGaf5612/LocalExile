// Walk-map capture (Frida, loaded by walkmaps.py into the running client).
//
// When the client generates an area it builds a terrain walkability grid before drawing it. Right after the grid
// provider returns (client 3.29.3b, VA 0x1410ddcfd, just after the vtable +0x50 call in 0x1410ddb90) rax points to
//   { i32 width; i32 height; vector<u8> cells [+0x08 begin, +0x10 end]; vector<u8> second [+0x20, +0x28] }.
// The first vector is width × height bytes, one per cell; the client walks on cells >= 2.
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
      } catch (e) {
        send({ type: 'error', message: String(e) });
      }
    });
    send({ type: 'ready', module: exe.name });
  }
}
