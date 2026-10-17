// Finds a UTF-16 string in the client's writable memory, then the 8-byte pointers to each hit's string start, to see
// which loaded structures hold it (e.g. a tile's .tmd object records). Asynchronous: results arrive as messages.
// Set NEEDLE before loading; standalone (no common.js needed).
const NEEDLE = typeof NEEDLE_OVERRIDE !== 'undefined' ? NEEDLE_OVERRIDE : '_area_transition2';
const MAX_HITS = 12, MAX_REFS = 12;
const m = Process.getModuleByName('Game.patched.exe');
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
const u16 = s => Array.from(s).map(c => c.charCodeAt(0).toString(16).padStart(2, '0') + ' 00').join(' ');
const ptrPat = p => { const h = p.toString(16).padStart(16, '0'); let o = []; for (let i = 14; i >= 0; i -= 2) o.push(h.substr(i, 2)); return o.join(' '); };
const ranges = Process.enumerateRanges({ protection: 'rw-', coalesce: true }).filter(r => r.size < 0x40000000);
function scanAll(pattern, onHit, done) {
  let i = 0;
  (function next() {
    if (i >= ranges.length) { done(); return; }
    const r = ranges[i++];
    Memory.scan(r.base, r.size, pattern, { onMatch: onHit, onError() {}, onComplete() { setTimeout(next, 0); } });
  })();
}
const starts = [];
scanAll(u16(NEEDLE), a => {
  if (starts.length >= MAX_HITS) return 'stop';
  let s = a;
  for (let k = 0; k < 200; k++) { let c; try { c = s.sub(2).readU16(); } catch (e) { break; } if (c < 0x20 || c > 0x7e) break; s = s.sub(2); }
  if (starts.some(x => x.equals(s))) return;
  starts.push(s);
  let t = '?'; try { t = s.readUtf16String().slice(0, 160); } catch (e) {}
  let before = '?'; try { before = s.sub(0x30).readByteArray(0x30); before = Array.from(new Uint8Array(before)).map(b => b.toString(16).padStart(2, '0')).join(''); } catch (e) {}
  send({ hit: s.toString(), str: t, before });
}, () => {
  send({ scanned: ranges.length, hits: starts.length });
  let j = 0;
  (function refs() {
    if (j >= starts.length) { send({ done: true }); return; }
    const s = starts[j++]; let n = 0;
    scanAll(ptrPat(s), a => {
      if (++n > MAX_REFS) return 'stop';
      let ctx = '?'; try { ctx = Array.from(new Uint8Array(a.sub(0x20).readByteArray(0x60))).map(b => b.toString(16).padStart(2, '0')).join(''); } catch (e) {}
      let vt = '?'; try { vt = va(a.sub(0x20).readPointer()); } catch (e) {}
      send({ ref: a.toString(), to: s.toString(), ctx });
    }, refs);
  })();
});
send({ started: NEEDLE, ranges: ranges.length });
