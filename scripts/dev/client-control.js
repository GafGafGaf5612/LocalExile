// Local development RPC for the copied standalone client. Loaded by client_control.py after client-resolver.js.
// Commands run inside the existing state-stack update, after its transition guard is entered.
// No input simulation, screen coordinates, credential persistence, or executable patching.
// Every client address and struct offset comes from client-signatures.json through resolveClient(); an operation
// whose symbols or offsets are missing fails with "Unsupported client: <op> needs <name> (not found)".
'use strict';
const module = Process.getModuleByName('Game.patched.exe');
let config = null, resolved = null;
const hooks = {};
const warnings = [];

// What each operation needs, besides `frame` (the hook every queued command runs in). Offsets are `.`-names.
const STATUS = ['app', 'stack', 'app.login', 'app.instance', 'stack.statesBegin', 'stack.statesEnd', 'state.id',
  'login.state', 'instance.state', 'login.account', 'login.character', 'stack.stateCount'];
const CHARACTERS = ['login.characters', 'character.stride', 'character.league', 'character.level', 'character.classId'];
const SEND = ['write', 'flush', 'instance.socket'];
const NEEDS = {
  status: STATUS,
  resources: ['lifeTick', 'poolUpdate', 'life.life', 'life.mana', 'life.es', 'pool.current', 'pool.fraction',
    'component.owner', 'owner.flags', 'update.current', 'update.pool'],
  characters: CHARACTERS,
  connect: ['connect', 'loginWriter', 'assignText', 'loginForm.account', 'loginForm.digest'],
  login: ['login'],
  play: ['play', 'push', 'stack.stateTable', 'ingame.classId', ...CHARACTERS],
  select: SEND,
  logout: ['logout', 'stack.stateTable'],
  chat: SEND,
  interaction: SEND,
};

function lookup(name) {
  if (resolved === null) return null;
  if (resolved.symbols[name]) return resolved.symbols[name].address;
  if (resolved.globals[name]) return resolved.globals[name].address;
  if (resolved.offsets[name]) return resolved.offsets[name].value;
  throw new Error(`Unknown client symbol ${name}`);
}
function missing(op) {
  const names = ['frame', ...(op === 'status' ? [] : STATUS), ...(NEEDS[op] || [])];
  return [...new Set(names)].filter(name => lookup(name) === null);
}
function need(op) {
  const absent = missing(op);
  if (absent.length) throw new Error(`Unsupported client: ${op} needs ${absent.join(', ')} (not found)`);
}
const sym = name => lookup(name);
const off = name => lookup(name);

const natives = {};
function native(name, args, returns = 'void') {
  natives[name] ??= new NativeFunction(sym(name), returns, args, {abi: 'win64', scheduling: 'cooperative'});
  return natives[name];
}
const connect = (...a) => native('connect', ['pointer', 'pointer', 'pointer'])(...a);
const login = (...a) => native('login', ['pointer', 'pointer', 'pointer', 'uint8', 'uint8'])(...a);
const play = (...a) => native('play', ['pointer', 'pointer', 'uint32'])(...a);
const write = (...a) => native('write', ['pointer', 'pointer', 'uint64'], 'pointer')(...a);
const flush = (...a) => native('flush', ['pointer', 'uint32', 'uint8'])(...a);
const logout = (...a) => native('logout', ['pointer'])(...a);
const push = (...a) => native('push', ['pointer', 'pointer'])(...a);
const assignText = (...a) => native('assignText', ['pointer', 'pointer', 'uint64'], 'pointer')(...a);

// Cheap layout checks: a wrong address or offset becomes a clear error instead of a crash or garbage.
function readable(p, what) {
  // A probing read is cheap; Process.findRangeByAddress walks every mapping (seconds in the game process).
  let ok = !p.isNull() && p.and(7).isNull();
  if (ok) { try { p.readU8(); } catch (_) { ok = false; } }
  if (!ok) throw new Error(`Client layout check failed: ${what} is not a readable object pointer (${p})`);
  return p;
}
function inRange(value, low, high, what) {
  if (!(value >= low && value <= high)) throw new Error(`Client layout check failed: ${what} = ${value} (expected ${low}-${high})`);
  return value;
}
const app = () => sym('app').readPointer();
const stack = () => sym('stack').readPointer();
const child = (owner, name) => {
  const p = owner.add(off(name)).readPointer();
  return p.isNull() ? p : readable(p, name);
};
const loginObject = () => child(readable(app(), 'app'), 'app.login');
const instance = () => child(readable(app(), 'app'), 'app.instance');
const loginState = l => inRange(l.add(off('login.state')).readU32(), 0, 3, 'login.state');
const instanceState = i => inRange(i.add(off('instance.state')).readU32(), 0, 3, 'instance.state');
rpc.exports.identity = () => ({pid: Process.id, path: module.path});
let lastLoginResult = null;
let credentials = null, loginSubmitted = false;
let life = null, resourceProbe = false;
const resourceUpdates = [];
const localLife = p => {
  if (p.isNull()) return false;
  const owner = p.add(off('component.owner')).readPointer();  // a bad pointer throws; callers catch it
  return !owner.isNull() && owner.add(off('owner.flags')).readU32() === 0x1000;
};
function resources() {
  if (life === null || !localLife(life) || status().instanceState !== 3) return null;
  const pools = Object.fromEntries([['life', 'life.life'], ['mana', 'life.mana'], ['es', 'life.es']].map(([name, offset]) => {
    const p = life.add(off(offset));
    return [name, {current: p.add(off('pool.current')).readS32(), fraction: p.add(off('pool.fraction')).readFloat()}];
  }));
  return {pools, updates: resourceUpdates.splice(0)};
}
function enableResources() {
  if (resourceProbe) return;
  resourceProbe = true;
  hooks.lifeTick = Interceptor.attach(sym('lifeTick'), {onEnter(args) {
    try { if (localLife(args[0])) life = args[0]; } catch (_) { life = null; }
  }});
  hooks.poolUpdate = Interceptor.attach(sym('poolUpdate'), {onEnter(args) {
    try {
      const owner = args[0].add(off('component.owner')).readPointer(); if (!localLife(owner)) return;
      resourceUpdates.push({timeMs: Date.now(), pool: args[1].add(off('update.pool')).readU8(),
        current: args[1].add(off('update.current')).readU32(), previousCurrent: args[0].add(off('pool.current')).readS32(),
        previousFraction: args[0].add(off('pool.fraction')).readFloat()});
      if (resourceUpdates.length > 128) resourceUpdates.shift();
    } catch (_) {}
  }});
}
function text(s, wide = true) {
  const length = s.add(0x10).readU64().toNumber(), cap = s.add(0x18).readU64().toNumber();
  if (length > 4096 || cap < length || cap > 0x100000) throw new Error('Invalid client string (length/capacity)');
  const data = cap > (wide ? 7 : 15) ? readable(s.readPointer(), 'string data') : s;
  return length ? (wide ? data.readUtf16String(length) : data.readUtf8String(length)) : '';
}
function string(s, wide, keep) {
  const header = Memory.alloc(32), data = wide ? Memory.allocUtf16String(s) : Memory.allocUtf8String(s);
  const length = wide ? s.length : unescape(encodeURIComponent(s)).length;
  header.writePointer(data); header.add(0x10).writeU64(length);
  header.add(0x18).writeU64(Math.max(length, wide ? 8 : 16));
  keep.push(header, data); return header;
}
function characters() {
  const l = loginObject();
  if (l.isNull() || loginState(l) !== 3) throw new Error('Not logged in');
  const vector = l.add(off('login.characters')), stride = off('character.stride');
  const begin = vector.readPointer(), end = vector.add(8).readPointer();
  const count = end.sub(begin).toInt32() / stride;
  if (!Number.isInteger(count) || count < 0 || count > 255) throw new Error('Invalid character vector');
  if (count) readable(begin, 'login.characters');
  return Array.from({length: count}, (_, index) => {
    const p = begin.add(index * stride);
    return {index, name: text(p), league: text(p.add(off('character.league'))),
      level: inRange(p.add(off('character.level')).readU8(), 1, 100, 'character.level'),
      classId: inRange(p.add(off('character.classId')).readU8(), 0, 15, 'character.classId')};
  });
}
function status() {
  const identity = {pid: Process.id, build: config.build, patchVersion: config.patchVersion};
  const a = app(), sm = stack();
  if (a.isNull() || sm.isNull()) return {...identity, screen: 'starting', states: [], loginState: 0, instanceState: 0};
  readable(a, 'app'); readable(sm, 'stack');
  const l = loginObject(), i = instance();
  const begin = sm.add(off('stack.statesBegin')).readPointer(), end = sm.add(off('stack.statesEnd')).readPointer();
  const count = end.sub(begin).toInt32() / 16;
  if (!Number.isInteger(count) || count < 0 || count > 32) throw new Error('Invalid state stack');
  const states = Array.from({length: count}, (_, n) =>
    inRange(readable(begin.add(n * 16).readPointer(), 'state').add(off('state.id')).readU8(), 0, off('stack.stateCount') - 1, 'state.id'));
  const login = l.isNull() ? 0 : loginState(l), area = i.isNull() ? 0 : instanceState(i);
  return {...identity, states, screen: ({4: 'game', 6: 'login', 7: 'login', 9: 'characters', 11: 'loading'})[states.at(-1)] || 'other',
    loginState: login, instanceState: area,
    account: login === 3 ? text(l.add(off('login.account'))) : null,
    character: area !== 3 ? null : text(l.add(off('login.character'))), lastLoginResult, loginSubmitted};
}
function stateEntry(id) {
  inRange(id, 0, off('stack.stateCount') - 1, 'state id');
  return stack().add(off('stack.stateTable') + id * 16);
}
function state(id) {
  const entry = stateEntry(id);
  if (entry.readPointer().isNull()) throw new Error('Client state is not constructed yet');
  push(stack(), entry);
}
function sendPacket(bytes, connection) {
  const p = Memory.alloc(bytes.length); p.writeByteArray(bytes);
  const owner = connection === 'login' ? loginObject() : instance();
  if (owner.isNull()) throw new Error('Connection is not open');
  const socket = owner.add(off(connection === 'login' ? 'login.socket' : 'instance.socket')).readPointer();
  if (socket.isNull()) throw new Error('Connection has no socket');
  readable(socket, 'socket');
  write(socket, p, bytes.length); flush(socket, 0, 0);
}
function execute(request) {
  need(request.op);
  const current = status(), keep = [];
  switch (request.op) {
    case 'status': return current;
    case 'resources':
      if (current.screen !== 'game' || current.instanceState !== 3) throw new Error('Resources require an active area');
      enableResources(); return resources();
    case 'characters': return {characters: characters()};
    case 'connect':
      if (current.screen !== 'login' || current.loginState !== 0) throw new Error('Connect requires a disconnected login screen');
      if (!Array.isArray(request.digest) || request.digest.length !== 32) throw new Error('Expected 32-byte digest');
      lastLoginResult = null;
      credentials = {account: request.account, digest: request.digest}; loginSubmitted = false;
      connect(loginObject(), string('127.0.0.1', true, keep), string(String(request.port), true, keep));
      return {submitted: true};
    case 'login': {
      // Login's handshake listener may already have submitted the form in the same frame as the 0x02 reply.
      if (loginSubmitted) return {alreadySubmitted: true};
      if (current.screen !== 'login' || current.loginState !== 2) throw new Error('Login requires a completed local handshake');
      if (!credentials) throw new Error('No pending login');
      const digest = Memory.alloc(32); digest.writeByteArray(credentials.digest);
      login(loginObject(), string(credentials.account, false, keep), digest, 0, 0);
      digest.writeByteArray(new Uint8Array(32));
      return {submitted: true};
    }
    case 'play': {
      if (current.screen !== 'characters' || current.loginState !== 3) throw new Error('Play requires character selection');
      const chosen = characters().find(c => c.name === request.name);
      if (!chosen) throw new Error('Character not found in the client list');
      const data = Memory.allocUtf16String(chosen.name), view = Memory.alloc(16);
      view.writePointer(data); view.add(8).writeU64(chosen.name.length);
      // The Play callback sets the class on state 4, but pushes state 11 (the loading state), not state 4.
      // Loading installs the instance-assignment listener before it enters the in-game state.
      const inGame = readable(stateEntry(4).readPointer(), 'in-game state');
      const classId = inGame.add(off('ingame.classId'));
      inRange(classId.readU32(), 0, 15, 'ingame.classId');
      classId.writeU32(chosen.classId);
      play(loginObject(), view, chosen.index); state(11);
      return {submitted: true, character: chosen.name};
    }
    case 'select':
      if (current.screen === 'characters') return {alreadySelected: true};
      if (current.screen !== 'game' || current.instanceState !== 3) throw new Error('Not playing a character');
      sendPacket([0x01, 0x53], 'instance'); return {submitted: true};
    case 'logout':
      if (current.screen === 'game') throw new Error('Return to character selection before logging out');
      logout(readable(stateEntry(7).readPointer(), 'login state')); return {submitted: true};
    case 'chat':
      if (current.screen !== 'game' || current.instanceState !== 3) throw new Error('Chat requires an active area');
      if (!Array.isArray(request.bytes) || request.bytes.length > 8192) throw new Error('Invalid chat message');
      sendPacket(request.bytes, 'instance'); return {submitted: true};
    case 'interaction': {
      if (current.screen !== 'game' || current.instanceState !== 3) throw new Error('Interaction requires an active area');
      const b = request.bytes, id = Array.isArray(b) && b.length >= 2 ? b[0] * 256 + b[1] : -1;
      const lengths = {0x24: 12, 0x25: 16, 0x28: 13, 0x2a: 18, 0x70: 6, 0x82: 3, 0x85: 2, 0x88: 15};
      if (!lengths[id] || b.length !== lengths[id])
        throw new Error('Unsupported interaction message');
      sendPacket(b, 'instance'); return {submitted: true};
    }
    default: throw new Error('Unsupported operation');
  }
}
let pending = null;
function attachHooks() {
  if (sym('frame') !== null) {
    hooks.frame = Interceptor.attach(sym('frame'), function () {
      if (!pending) return;
      const work = pending; pending = null; clearTimeout(work.timer);
      // Do not let a one-shot caller detach while this native hook is still unwinding.
      try { const result = execute(work.request); setImmediate(() => work.resolve(result)); }
      catch (e) { setImmediate(() => work.reject(e)); }
    });
  } else {
    warnings.push('Unsupported client: frame (the state-stack update hook) was not found, so no command can run; see bridge:doctor');
  }
  if (sym('dispatch') !== null && off('message.id') !== null && off('message.loginResult') !== null) {
    hooks.dispatch = Interceptor.attach(sym('dispatch'), {onEnter(args) {
      try {
        const message = args[2].readPointer();
        if (!message.isNull() && message.add(off('message.id')).readU16() === 4) lastLoginResult = message.add(off('message.loginResult')).readU16();
      } catch (_) {}
    }});
  } else {
    warnings.push('dispatch was not found: a rejected login is reported as a timeout instead of its result code');
  }
  if (missing('connect').length === 0) {
    hooks.loginWriter = Interceptor.attach(sym('loginWriter'), {onEnter(args) {
      if (!credentials) return;
      // Substitute the requested credentials in the one outgoing native form submission. The ordinary handshake
      // callback can race the RPC caller; replacing this message prevents an earlier saved/empty password winning.
      const value = credentials; credentials = null;
      const data = Memory.allocUtf8String(value.account);
      assignText(args[0].add(off('loginForm.account')), data, unescape(encodeURIComponent(value.account)).length);
      args[0].add(off('loginForm.digest')).writeByteArray(value.digest);
      value.digest.fill(0); loginSubmitted = true;
    }});
  }
}
rpc.exports.init = options => {
  if (resolved !== null) return {warnings, unresolved: unresolvedNames()};
  config = options;
  resolved = resolveClient(module, options.signatures, options.mode || 'pinned');
  attachHooks();
  return {warnings, unresolved: unresolvedNames(), elapsedMs: resolved.elapsedMs};
};
function unresolvedNames() {
  return [...Object.entries(resolved.symbols), ...Object.entries(resolved.globals)]
    .filter(([, v]) => v.address === null).map(([name]) => name)
    .concat(Object.entries(resolved.offsets).filter(([, v]) => v.value === null).map(([name]) => name));
}
rpc.exports.doctor = () => {
  const strip = value => value === null ? null : '0x' + value.toString(16);
  const ops = Object.fromEntries(Object.keys(NEEDS).map(op => {
    const absent = missing(op);
    return [op, absent.length ? `needs ${absent.join(', ')} (not found)` : 'ok'];
  }));
  const sanity = {};
  const probe = (name, fn) => { try { sanity[name] = fn(); } catch (e) { sanity[name] = `failed: ${e.message}`; } };
  probe('app', () => missing('status').includes('app') ? 'not resolved' : (app().isNull() ? 'null (starting)' : `${readable(app(), 'app')} readable`));
  probe('stack', () => missing('status').includes('stack') ? 'not resolved' : (stack().isNull() ? 'null (starting)' : `${readable(stack(), 'stack')} readable`));
  probe('status', () => missing('status').length ? 'not checked' : (({screen, states, loginState, instanceState}) =>
    `screen ${screen}, states [${states}], login.state ${loginState}, instance.state ${instanceState} (all in range)`)(status()));
  return {
    mode: resolved.mode, elapsedMs: resolved.elapsedMs, imageBase: resolved.imageBase, moduleBase: resolved.moduleBase,
    warnings, hooks: Object.fromEntries(['frame', 'dispatch', 'loginWriter', 'lifeTick', 'poolUpdate']
      .map(name => [name, hooks[name] ? 'attached' : (sym(name) === null ? 'not found' : (name === 'lifeTick' || name === 'poolUpdate' ? 'on first game:resources' : 'not attached'))])),
    ops, sanity,
    symbols: Object.fromEntries(Object.entries(resolved.symbols).map(([name, s]) =>
      [name, {method: s.method, va: s.va, pinned: s.pinned, ...(s.detail ? {detail: s.detail} : {})}])),
    globals: Object.fromEntries(Object.entries(resolved.globals).map(([name, g]) =>
      [name, {method: g.method, va: g.va, pinned: g.pinned, detail: g.detail}])),
    offsets: Object.fromEntries(Object.entries(resolved.offsets).map(([name, o]) =>
      [name, {method: o.method, value: strip(o.value), pinned: o.pinned, detail: o.detail}])),
  };
};
rpc.exports.command = request => new Promise((resolve, reject) => {
  if (resolved === null) { reject(new Error('Bridge not initialised')); return; }
  try { need(request.op); } catch (e) { reject(e); return; }
  if (pending) { reject(new Error('Another command is pending')); return; }
  pending = {request, resolve, reject, timer: setTimeout(() => {
    pending = null; reject(new Error('No client frame within 5 seconds; command cancelled'));
  }, 5000)};
});
