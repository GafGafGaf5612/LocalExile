// What the client asks the web: every outbound socket send, reduced to the one thing that is visible without
// decrypting anything — a plaintext HTTP request line + Host header, or the SNI host name in a TLS ClientHello.
// Also logs where it connects (connect/WSAConnect) and the names it resolves (getaddrinfo/GetAddrInfoW).
// Answers "which store endpoint does the shop use" (issue: microtransaction shop) with no hooks inside the client's
// own code and no Stalker, so it stays safe to attach while playing. Load after common.js if you want its helpers.
// Run with: python scripts/re/frida/run.py Game.patched.exe scripts/re/frida/trace-http.js --log logs/shop-trace.log
const ws2 = Process.getModuleByName('ws2_32.dll');
const exportOf = name => { try { return ws2.getExportByName(name); } catch (e) { return null; } };

const peer = new Map();          // socket handle string → "ip:port" from the last connect
const hello = new Map();         // socket handle string → { buf, need } while a ClientHello record is incomplete
const seen = new Set();          // "host" names already reported, so a busy socket logs once
const LOOPBACK = /^(127\.|::1$)/;

const ascii = (p, n) => {
  const out = [];
  for (let i = 0; i < n; i++) {
    const b = p.add(i).readU8();
    out.push(b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.');
  }
  return out.join('');
};

function endpoint(name) {
  try {
    const family = name.readU16();
    const port = (name.add(2).readU8() << 8) | name.add(3).readU8();
    if (family === 2) return [0, 1, 2, 3].map(i => name.add(4 + i).readU8()).join('.') + ':' + port;
    if (family === 23) {
      const groups = [];
      for (let i = 0; i < 16; i += 2) groups.push(((name.add(8 + i).readU8() << 8) | name.add(9 + i).readU8()).toString(16));
      return groups.join(':') + ':' + port;
    }
    return 'family ' + family + ':' + port;
  } catch (e) { return '?'; }
}

// The ClientHello's server_name extension (type 0), if this buffer holds the whole handshake record.
function sniOf(bytes) {
  try {
    if (bytes.length < 5 || bytes[0] !== 0x16 || bytes[1] !== 0x03) return null;
    const record = (bytes[3] << 8) | bytes[4];
    if (bytes.length < 5 + record) return null;
    let i = 5;
    if (bytes[i] !== 0x01) return null;                       // handshake type ClientHello
    i += 4 + 2 + 32;                                          // header, version, random
    i += 1 + bytes[i];                                        // session id
    i += 2 + ((bytes[i] << 8) | bytes[i + 1]);                // cipher suites
    i += 1 + bytes[i];                                        // compression methods
    if (i + 2 > bytes.length) return null;
    const end = i + 2 + ((bytes[i] << 8) | bytes[i + 1]);
    i += 2;
    while (i + 4 <= end && i + 4 <= bytes.length) {
      const type = (bytes[i] << 8) | bytes[i + 1];
      const len = (bytes[i + 2] << 8) | bytes[i + 3];
      i += 4;
      if (type === 0) {
        const nameLen = (bytes[i + 3] << 8) | bytes[i + 4];
        return String.fromCharCode.apply(null, bytes.slice(i + 5, i + 5 + nameLen));
      }
      i += len;
    }
  } catch (e) { }
  return null;
}

function looksHttp(p, n) {
  if (n < 16) return false;
  const head = ascii(p, Math.min(n, 10));
  return /^(GET|POST|HEAD|PUT|DELETE|OPTIONS|PATCH|CONNECT) /.test(head);
}

function logHttp(sock, p, n) {
  const text = ascii(p, Math.min(n, 2048));
  const eol = text.indexOf('\r\n');
  const request = eol < 0 ? text.slice(0, 200) : text.slice(0, eol);
  const host = /(?:^|\r\n)Host: *([^\r\n]+)/i.exec(text);
  const key = 'http ' + (host ? host[1] : request);
  if (seen.has(key)) return;
  seen.add(key);
  send({ http: request, host: host ? host[1] : null, peer: peer.get(sock) || null });
}

function logTls(sock, p, n) {
  let entry = hello.get(sock);
  if (!entry) { entry = { buf: [], need: 0 }; hello.set(sock, entry); }
  for (let i = 0; i < n && entry.buf.length < 8192; i++) entry.buf.push(p.add(i).readU8());
  if (entry.buf.length >= 5) entry.need = 5 + ((entry.buf[3] << 8) | entry.buf[4]);
  if (entry.need === 0 || entry.buf.length < entry.need) return;
  hello.delete(sock);
  const host = sniOf(entry.buf);
  const key = 'tls ' + host;
  if (!host || seen.has(key)) return;
  seen.add(key);
  send({ tls: host, peer: peer.get(sock) || null });
}

function inspect(sock, p, n) {
  try {
    if (n <= 0) return;
    if (looksHttp(p, n)) logHttp(sock, p, n);
    else if (p.readU8() === 0x16 && n > 5 && p.add(1).readU8() === 0x03) logTls(sock, p, n);
  } catch (e) { send({ error: String(e) }); }
}

const connect = exportOf('connect');
if (connect) Interceptor.attach(connect, {
  onEnter(args) { this.sock = args[0].toString(); this.to = endpoint(args[1]); peer.set(this.sock, this.to); },
  onLeave(ret) { if (!LOOPBACK.test(this.to)) send({ connect: this.to, ok: ret.toInt32() === 0 }); },
});

const wsaConnect = exportOf('WSAConnect');
if (wsaConnect) Interceptor.attach(wsaConnect, {
  onEnter(args) { this.sock = args[0].toString(); this.to = endpoint(args[1]); peer.set(this.sock, this.to); },
  onLeave(ret) { if (!LOOPBACK.test(this.to)) send({ connect: this.to, ok: ret.toInt32() === 0 }); },
});

const sendFn = exportOf('send');
if (sendFn) Interceptor.attach(sendFn, {
  onEnter(args) { inspect(args[0].toString(), args[1], args[2].toInt32()); },
});

const sendTo = exportOf('sendto');
if (sendTo) Interceptor.attach(sendTo, {
  onEnter(args) { inspect(args[0].toString(), args[1], args[2].toInt32()); },
});

// WSABUF { u32 len; char* buf } is 16 bytes on x64: the pointer sits at +8.
const wsaSend = exportOf('WSASend');
if (wsaSend) Interceptor.attach(wsaSend, {
  onEnter(args) {
    try {
      const sock = args[0].toString();
      const count = args[2].toInt32();
      for (let i = 0; i < count; i++) {
        const buf = args[1].add(i * 16);
        const len = buf.readU32();
        if (len > 0) inspect(sock, buf.add(8).readPointer(), len);
      }
    } catch (e) { send({ error: String(e) }); }
  },
});

for (const name of ['getaddrinfo', 'GetAddrInfoW']) {
  const fn = exportOf(name);
  if (!fn) continue;
  Interceptor.attach(fn, {
    onEnter(args) {
      try {
        const host = name === 'GetAddrInfoW' ? args[0].readUtf16String() : args[0].readUtf8String();
        if (host && !seen.has('dns ' + host)) { seen.add('dns ' + host); send({ dns: host }); }
      } catch (e) { }
    },
  });
}

send('trace-http loaded: connect, send, WSASend, getaddrinfo');
