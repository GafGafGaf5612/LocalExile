// Resolves scripts/dev/client-signatures.json inside the attached client. client_control.py loads this file
// before client-control.js in one Frida script; scripts/ci/resolve_client.py implements the same rules offline.
// The pinned values come from the known build (`builds`) whose pinned symbols the module matches best. Per symbol:
// (1) its pinned VA, if the signature matches there, (2) the wildcarded signature, exactly once, (3) the call target
// captured in a resolved caller, or the one function referencing a unique string, (4) unresolved. Globals and struct
// offsets are read back from the captured operands of resolved symbols; an offset whose sources are all unresolved
// falls back to its pinned value, marked unverified; globals do not (a moved function means moved data).
'use strict';
const INSIDE_WINDOW = 0x4000;  // bytes searched after a parent function's start for an `inside:` anchor

function resolveClient(module, signatures, mode) {
  const started = Date.now();
  const base = module.base;
  let imageBase = 0x140000000;  // set from the pinned build below; image VAs stay below 2^53
  const rvaOf = p => p.sub(base).toUInt32();
  const vaOf = p => p === null ? null : '0x' + (imageBase + rvaOf(p)).toString(16);
  const code = module.enumerateRanges('r-x').map(r => ({base: r.base, size: r.size}));
  const pdata = exceptionTable(base);

  function functionOf(p) {
    // Primary RUNTIME_FUNCTION containing p (following UNW_FLAG_CHAININFO), as {begin, end} pointers.
    if (pdata === null) return null;
    const rva = rvaOf(p);
    let lo = 0, hi = pdata.count - 1, found = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1, begin = pdata.at.add(mid * 12).readU32();
      if (begin <= rva) { found = mid; lo = mid + 1; } else hi = mid - 1;
    }
    if (found < 0) return null;
    let entry = pdata.at.add(found * 12);
    if (rva >= entry.add(4).readU32()) return null;
    for (let depth = 0; depth < 32; depth++) {
      const unwind = base.add(entry.add(8).readU32());
      if (!((unwind.readU8() >> 3) & 4)) return {begin: base.add(entry.readU32()), end: base.add(entry.add(4).readU32())};
      entry = unwind.add(4 + 2 * ((unwind.add(2).readU8() + 1) & ~1));
    }
    return null;
  }

  const specs = signatures.symbols, symbols = {}, reasons = {};
  for (const name of Object.keys(specs)) reasons[name] = [];
  const note = (name, why) => { if (why && !reasons[name].includes(why)) reasons[name].push(why); };

  function matchesAt(p, pattern) {
    const tokens = pattern.split(' ');
    if (p.compare(base) < 0 || rvaOf(p) + tokens.length > module.size) return false;
    let actual;
    try { actual = new Uint8Array(p.readByteArray(tokens.length)); } catch (_) { return false; }
    return tokens.every((t, i) => t === '??' || parseInt(t, 16) === actual[i]);
  }
  const pinnedStart = (build, name) => {
    const va = build.symbols[name];
    return va === undefined ? null
      : base.add(parseInt(va, 16) - parseInt(build.imageBase, 16) - (specs[name].offset || 0));
  };

  // The known build this module is: the one whose pinned symbols match best (Game.patched.exe differs from the
  // official PathOfExile.exe the builds are keyed by only in the login patch). Its values are the pinned ones.
  const builds = signatures.builds || {};
  let buildKey = null, buildHits = 0;
  for (const [key, build] of Object.entries(builds)) {
    const hits = Object.keys(build.symbols).filter(name => specs[name] && pinnedStart(build, name) !== null &&
      matchesAt(pinnedStart(build, name), specs[name].pattern)).length;
    if (hits > buildHits) { buildKey = key; buildHits = hits; }
  }
  const pins = buildKey === null ? null : builds[buildKey];
  // Constants come from the matching build, else from the newest one listed.
  const constants = pins ?? builds[Object.keys(builds).at(-1)] ?? {symbols: {}, globals: {}, offsets: {}};
  // The loader rewrites ImageBase in the mapped header, so image VAs use the pinned build's base.
  if (constants.imageBase) imageBase = parseInt(constants.imageBase, 16);

  function readCapture(start, capture) {
    // Value from the bytes, cross-checked against the decoded instruction (Instruction.parse).
    const field = start.add(capture.at);
    let fromBytes;
    if (capture.kind === 'rip' || capture.kind === 'rel32') {
      fromBytes = start.add(capture.end ?? capture.at + capture.size).add(field.readS32());
    } else if (capture.kind === 'disp') {
      fromBytes = capture.size === 1 ? field.readS8() : capture.size === 2 ? field.readS16() : field.readS32();
    } else {
      fromBytes = capture.size === 1 ? field.readU8() : capture.size === 2 ? field.readU16() : field.readU32();
    }
    if (capture.insn === undefined) return fromBytes;
    const insn = Instruction.parse(start.add(capture.insn));
    let decoded = null;
    for (const op of insn.operands) {
      if (capture.kind === 'rel32' && op.type === 'imm') decoded = ptr('0x' + op.value.toString(16));
      else if (capture.kind === 'rip' && op.type === 'mem' && op.value.base === 'rip')
        decoded = insn.next.add(op.value.disp);
      else if (capture.kind === 'disp' && op.type === 'mem' && op.value.base !== 'rip' && op.value.base !== undefined)
        decoded = op.value.disp;
      else if (capture.kind === 'imm' && op.type === 'imm') decoded = Number(op.value.toString());
    }
    const same = decoded !== null && (typeof fromBytes === 'number' ? decoded === fromBytes : decoded.equals(fromBytes));
    if (!same) throw new Error(`capture at +${capture.at} decodes as ${insn} (bytes give ${fromBytes})`);
    return fromBytes;
  }

  function locationOk(name, start) {
    const check = specs[name].start;
    if (!check || pdata === null) return [true, ''];
    if (check === 'function') {
      const f = functionOf(start);
      return [f !== null && f.begin.equals(start), 'not a function start'];
    }
    const parent = symbols[check.slice(7)];
    if (!parent) return [true, `${check.slice(7)} unresolved; not checked`];
    const mine = functionOf(start), theirs = functionOf(parent.start);
    return [mine !== null && theirs !== null && mine.begin.equals(theirs.begin), `not inside ${check.slice(7)}`];
  }

  function accept(name, start, method, detail = '') {
    const spec = specs[name];
    const [ok, why] = locationOk(name, start);
    if (!ok) return [false, why];
    const captures = {};
    const details = detail ? [detail] : [];
    if (matchesAt(start, spec.pattern)) {
      for (const [cap, capture] of Object.entries(spec.captures || {})) {
        try { captures[cap] = readCapture(start, capture); }
        catch (e) { details.push(`${cap}: ${e.message}`); }
      }
    } else if (method === 'pinned' || method === 'signature') {
      return [false, 'pattern mismatch'];
    } else {
      details.push('pattern differs here, captures unavailable');
    }
    if (why.endsWith('not checked')) details.push(why);
    symbols[name] = {address: start.add(spec.offset || 0), start, method, detail: details.join('; '), captures};
    return [true, ''];
  }

  const hasXref = name => Boolean((specs[name].callers || []).length || specs[name].string);
  const parentPending = name => (specs[name].start || '').startsWith('inside:') && !symbols[specs[name].start.slice(7)];

  function tryPinned(name) {
    const start = pins === null ? null : pinnedStart(pins, name);
    if (start === null) return [false, 'no pinned address'];
    if (!matchesAt(start, specs[name].pattern)) return [false, 'pinned address does not match'];
    return accept(name, start, 'pinned');
  }

  function trySignature(name) {
    const spec = specs[name], check = spec.start || '';
    let ranges = code;
    const parent = check.startsWith('inside:') ? symbols[check.slice(7)] : null;
    if (parent) ranges = [{base: parent.start, size: INSIDE_WINDOW}];
    const found = [];
    for (const r of ranges) for (const m of Memory.scanSync(r.base, r.size, spec.pattern)) found.push(m.address);
    if (found.length !== 1) return [false, `signature matched ${found.length} times`];
    return accept(name, found[0], 'signature');
  }

  function stringFunction(text) {
    const hits = stringHits(module, text);
    if (hits.length !== 1) return [null, `string found ${hits.length} times`];
    const functions = new Map();
    for (const r of code) for (const m of Memory.scanSync(r.base, r.size, '48 8d 05 : fb ff c7')) {
      if (m.address.add(7).add(m.address.add(3).readS32()).equals(hits[0])) {
        const f = functionOf(m.address);
        functions.set(f === null ? 'none' : f.begin.toString(), f);
      }
    }
    if (functions.size !== 1 || functions.has('none')) return [null, `string referenced from ${functions.size} functions`];
    return [functions.values().next().value.begin, 'string reference'];
  }

  function tryXref(name) {
    const spec = specs[name], targets = new Map();
    for (const source of spec.callers || []) {
      const [caller, cap] = source.split('.');
      const value = symbols[caller]?.captures[cap];
      if (value !== undefined) {
        const key = value.toString();
        targets.set(key, [...(targets.get(key) || []), caller]);
      }
    }
    if (targets.size > 1) return [false, 'callers disagree: ' + [...targets.keys()].map(t => vaOf(ptr(t))).join(', ')];
    if (targets.size === 1) {
      const [[target, callers]] = targets;
      return accept(name, ptr(target).sub(spec.offset || 0), 'xref', 'call target in ' + callers.join(', '));
    }
    if (spec.string) {
      const [begin, why] = stringFunction(spec.string);
      if (begin === null) return [false, why];
      return accept(name, begin, 'string', `references "${spec.string}"`);
    }
    return [false, 'no resolved caller'];
  }

  function attempt(fn, name) {
    try { const [ok, why] = fn(name); if (!ok) note(name, why); return ok; }
    catch (e) { note(name, e.message); return false; }
  }

  function follow() {
    let progress = true;
    while (progress) {
      progress = false;
      for (const name of Object.keys(specs)) {
        if (!symbols[name] && hasXref(name) && attempt(tryXref, name)) progress = true;
      }
    }
  }

  const deferred = [];
  for (const name of Object.keys(specs)) {
    if (mode === 'pinned' && attempt(tryPinned, name)) continue;
    if ((mode === 'xref' && hasXref(name)) || parentPending(name)) { deferred.push(name); continue; }
    attempt(trySignature, name);
  }
  follow();
  for (const name of deferred) if (!symbols[name]) attempt(trySignature, name);
  follow();

  const out = {mode, imageBase: '0x' + imageBase.toString(16), moduleBase: base.toString(), symbols: {},
    globals: {}, offsets: {},
    build: pins === null ? null : {sha256: buildKey, version: pins.version, label: pins.label,
      matched: `${buildHits} of ${Object.keys(specs).length} pinned symbols`}};
  const pinnedValue = (table, name) => constants[table][name] ?? null;
  for (const name of Object.keys(specs)) {
    const s = symbols[name];
    out.symbols[name] = s ? {address: s.address, start: s.start, va: vaOf(s.address), method: s.method,
      detail: s.detail, captures: s.captures, pinned: pinnedValue('symbols', name)}
      : {address: null, va: null, method: 'unresolved', detail: reasons[name].join('; '), captures: {},
        pinned: pinnedValue('symbols', name)};
  }
  const sourceValues = sources => {
    const values = [], missingSources = [];
    for (const source of sources) {
      const [name, cap] = source.split('.');
      const value = out.symbols[name].captures[cap];
      if (value === undefined) missingSources.push(source); else values.push([source, value]);
    }
    return [values, missingSources];
  };
  for (const [name, spec] of Object.entries(signatures.globals || {})) {
    const [values] = sourceValues([spec.from]);
    out.globals[name] = values.length
      ? {address: values[0][1], va: vaOf(values[0][1]), method: out.symbols[spec.from.split('.')[0]].method,
        detail: `from ${spec.from}`, pinned: pinnedValue('globals', name)}
      : {address: null, va: null, method: 'unresolved', detail: `${spec.from} unresolved`,
        pinned: pinnedValue('globals', name)};
  }
  for (const [name, spec] of Object.entries(signatures.offsets || {})) {
    const sources = typeof spec.from === 'string' ? [spec.from] : (spec.from || []);
    const [values, missingSources] = sourceValues(sources);
    const adjusted = values.map(([source, value]) => [source, value + (spec.add || 0)]);
    const distinct = [...new Set(adjusted.map(([, value]) => value))];
    const pinned = pinnedValue('offsets', name);
    if (distinct.length > 1) {
      out.offsets[name] = {value: null, method: 'unresolved', unverified: false, pinned,
        detail: 'sources disagree: ' + adjusted.map(([s, v]) => `${s}=0x${v.toString(16)}`).join(', ')};
    } else if (distinct.length === 1) {
      out.offsets[name] = {value: distinct[0], method: out.symbols[adjusted[0][0].split('.')[0]].method,
        unverified: false, detail: 'from ' + adjusted.map(([s]) => s).join(', '), pinned};
    } else if (pinned === null) {
      out.offsets[name] = {value: null, method: 'unresolved', unverified: false, pinned,
        detail: 'no pinned value' + (sources.length ? ', sources unresolved' : '')};
    } else {
      // A derived offset whose code was not found keeps its pinned value, unverified: operations that call or
      // write treat it as missing (client-control.js).
      out.offsets[name] = {value: parseInt(pinned, 16), method: 'constant', unverified: sources.length > 0, pinned,
        detail: sources.length ? 'unverified: sources unresolved: ' + missingSources.join(', ') : 'pinned constant'};
    }
  }
  out.elapsedMs = Date.now() - started;
  return out;
}

function stringHits(module, text) {
  // A NUL-terminated ASCII string in the module's non-executable ranges, preceded by a NUL or a range start
  // (resolve_client.py searches the same sections).
  const bytes = Array.from(text, c => c.charCodeAt(0).toString(16).padStart(2, '0')).concat(['00']).join(' ');
  const hits = [];
  for (const r of module.enumerateRanges('r--').filter(range => range.protection[2] !== 'x')) {
    for (const m of Memory.scanSync(r.base, r.size, bytes)) {
      if (m.address.equals(r.base) || m.address.sub(1).readU8() === 0) hits.push(m.address);
    }
  }
  return hits;
}

function exceptionTable(base) {
  // The PE exception directory (.pdata): RUNTIME_FUNCTION {begin, end, unwind} RVAs, sorted by begin.
  try {
    const pe = base.add(base.add(0x3c).readU32());
    const optional = pe.add(24);
    if (optional.readU16() !== 0x20b || optional.add(108).readU32() <= 3) return null;
    const rva = optional.add(112 + 8 * 3).readU32(), size = optional.add(112 + 8 * 3 + 4).readU32();
    return rva && size ? {at: base.add(rva), count: Math.floor(size / 12)} : null;
  } catch (_) {
    return null;
  }
}
