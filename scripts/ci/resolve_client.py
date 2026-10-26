"""Resolve the native control bridge's client symbols in a client executable, offline (standard library only).

    python -I scripts/ci/resolve_client.py PathOfExile.exe                    # report only
    python -I scripts/ci/resolve_client.py PathOfExile.exe --write --version 3.29.4.1 --report report.md
    python -I scripts/ci/resolve_client.py Client/Game.patched.exe Client/Game.exe --check
    python -I scripts/ci/resolve_client.py Client/Game.exe --check --mode xref

scripts/dev/client-signatures.json holds everything the bridge (scripts/dev/client-control.js) knows about the
client. `symbols`, `globals` and `offsets` say how to find each thing in any build: per symbol a wildcarded
signature (`??` for bytes a patch moves) with captures of the operands the bridge reads (call targets, RIP-relative
globals, struct offsets) and optional caller/string cross-references; per global and offset the capture it is
derived from. `builds` pins what was found in each known build, keyed by the SHA-256 of the official
PathOfExile.exe: version, build label, image base, and the VA or value of every symbol, global and offset.
This script reads the executable in place (mmap, no copy), picks its build (by SHA-256, else the build whose pinned
symbols it matches best, as the bridge does) and resolves every symbol the way the bridge does at attach:

1. pinned: the signature matches at the build's pinned VA;
2. signature: the pattern occurs exactly once (anchors marked `inside:<symbol>` are searched inside that function);
3. xref: the call target captured in a resolved caller, or the one function that references a unique string;
4. otherwise unresolved.

Captures are always read again from the code, and must sit behind a matching opcode or ModRM byte (the bridge
decodes the instruction instead). An offset whose sources are all unresolved keeps its pinned value, unverified.
`--write` adds this executable's build (or replaces the entry with the same SHA-256; other builds are kept), with
`--version`; `--report` writes a markdown table against the build it was compared with. Exit status: 0 the build
was already listed unchanged, 10 added or updated with everything resolved, 20 added with something unresolved
(1 and 2 remain Python's own error and usage codes). `--check` instead verifies, for each executable given, that
every symbol resolves by signature (`--mode xref`: by cross-reference where one exists) to exactly its build's
pinned VA, with the same globals and offsets (exit 0/1).
"""
import argparse
import hashlib
import json
import mmap
from pathlib import Path
import re
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
SIGNATURES = ROOT / "scripts" / "dev" / "client-signatures.json"
EXECUTE = 0x20000000
INSIDE_WINDOW = 0x4000  # bytes searched after the parent function's start for an `inside:` anchor
# lea r64, [rip+disp32]: REX.W (48/4C), 8D, ModRM with mod=00 rm=101.
LEA_RIP = re.compile(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]")


class Image:
    """A PE32+ executable read in place (bytes, bytearray or a read-only mmap; never copied). Addresses are RVAs,
    translated to file offsets through the section table; .pdata is searched lazily."""

    def __init__(self, data):
        self.data = data
        if data[:2] != b"MZ":
            raise ValueError("not a PE file (no MZ header)")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("not a PE file (no PE signature)")
        count = struct.unpack_from("<H", data, pe + 6)[0]
        optional_size = struct.unpack_from("<H", data, pe + 20)[0]
        optional = pe + 24
        if struct.unpack_from("<H", data, optional)[0] != 0x20B:
            raise ValueError("not a PE32+ (64-bit) image")
        self.base = struct.unpack_from("<Q", data, optional + 24)[0]
        directories = struct.unpack_from("<I", data, optional + 108)[0]
        exception = struct.unpack_from("<II", data, optional + 112 + 8 * 3) if directories > 3 else (0, 0)
        self.sections = []  # (name, rva, size present in the file, file offset, characteristics)
        table = optional + optional_size
        for i in range(count):
            name, vsize, rva, raw_size, raw = struct.unpack_from("<8sIIII", data, table + 40 * i)
            characteristics = struct.unpack_from("<I", data, table + 40 * i + 36)[0]
            size = max(0, min(raw_size, vsize or raw_size, len(data) - raw))
            self.sections.append((name.rstrip(b"\0").decode("ascii", "replace"), rva, size, raw, characteristics))
        self.pdata = None
        if exception[0] and exception[1] and self.offset(exception[0], exception[1]) is not None:
            self.pdata = (self.offset(exception[0], exception[1]), exception[1] // 12)

    @classmethod
    def open(cls, path):
        with open(path, "rb") as file:
            return cls(mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ))

    def offset(self, rva, size=1):
        """File offset of [rva, rva+size), or None if that is not all in one section's file data."""
        for _, start, length, raw, _ in self.sections:
            if start <= rva and rva + size <= start + length:
                return raw + rva - start
        return None

    def read(self, rva, size):
        at = self.offset(rva, size)
        return None if at is None else bytes(self.data[at:at + size])

    def code_ranges(self):
        return [(rva, size) for _, rva, size, _, flags in self.sections if flags & EXECUTE]

    def data_ranges(self):
        return [(rva, size) for _, rva, size, _, flags in self.sections if not flags & EXECUTE]

    def function(self, rva):
        """(begin, end) of the primary function containing rva, following chained unwind info; None if unknown."""
        if self.pdata is None:
            return None
        at, count = self.pdata
        low, high, found = 0, count - 1, -1
        while low <= high:  # binary search over the RUNTIME_FUNCTION table (sorted by begin)
            middle = (low + high) // 2
            if struct.unpack_from("<I", self.data, at + 12 * middle)[0] <= rva:
                found, low = middle, middle + 1
            else:
                high = middle - 1
        if found < 0:
            return None
        begin, end, unwind = struct.unpack_from("<III", self.data, at + 12 * found)
        if rva >= end:
            return None
        for _ in range(32):
            info = self.read(unwind, 4)
            if info is None:
                return None
            if not info[0] >> 3 & 4:  # UNW_FLAG_CHAININFO: the primary RUNTIME_FUNCTION follows the unwind codes
                return begin, end
            chained = self.read(unwind + 4 + 2 * ((info[2] + 1) & ~1), 12)
            if chained is None:
                return None
            begin, end, unwind = struct.unpack("<III", chained)
        return None

    def build_label(self):
        for _, _, size, raw, flags in self.sections:  # data sections only: no need to page in the code
            if not flags & EXECUTE and (label := build_label(memoryview(self.data)[raw:raw + size])):
                return label
        return None


class Pattern:
    """A signature: hex bytes and `??` wildcards. Searched by its longest literal run (C-speed find), then verified."""
    cache = {}

    def __new__(cls, text):
        if text not in cls.cache:
            tokens = text.split()
            if not tokens or any(t != "??" and not re.fullmatch(r"[0-9a-fA-F]{2}", t) for t in tokens):
                raise ValueError(f"invalid pattern: {text!r}")
            self = super().__new__(cls)
            self.length = len(tokens)
            self.regex = re.compile(b"".join(b"." if t == "??" else re.escape(bytes.fromhex(t)) for t in tokens), re.S)
            runs = re.finditer(r"(?:[0-9a-fA-F]{2} ?)+", text + " ")
            best = max(runs, key=lambda m: len(m.group().split()), default=None)
            self.anchor_at = len(text[:best.start()].split()) if best else 0
            self.anchor = bytes.fromhex(best.group().replace(" ", "")) if best else b""
            cls.cache[text] = self
        return cls.cache[text]

    def at(self, data, offset):
        return offset >= 0 and self.regex.fullmatch(data, offset, offset + self.length) is not None

    def find(self, data, start, end):
        """File offsets of every match inside [start, end)."""
        if not self.anchor:
            return [m.start() for m in self.regex.finditer(data, start, end)]
        found, position = [], start + self.anchor_at
        while (hit := data.find(self.anchor, position, end)) >= 0:
            if hit - self.anchor_at + self.length <= end and self.at(data, hit - self.anchor_at):
                found.append(hit - self.anchor_at)
            position = hit + 1
        return found


def compile_pattern(pattern):
    compiled = Pattern(pattern)
    return compiled.regex, compiled.length


def matches_at(image, rva, pattern):
    compiled = Pattern(pattern)
    at = image.offset(rva, compiled.length)
    return at is not None and compiled.at(image.data, at)


def scan(image, pattern, ranges):
    """RVAs of the pattern's matches in the given (rva, size) ranges."""
    compiled, found = Pattern(pattern), []
    for rva, size in ranges:
        for _, start, length, raw, _ in image.sections:
            low, high = max(rva, start), min(rva + size, start + length)
            if low < high:
                found += [start + at - raw for at in compiled.find(image.data, raw + low - start, raw + high - start)]
    return found


def string_hits(image, text):
    """RVAs of a NUL-terminated ASCII string in the non-executable sections, preceded by a NUL or a section start
    (client-resolver.js searches the same sections)."""
    needle = text.encode("ascii") + b"\0"
    hits = []
    for _, start, length, raw, flags in image.sections:
        if flags & EXECUTE:
            continue
        position = raw
        while (at := image.data.find(needle, position, raw + length)) >= 0:
            if at == raw or image.data[at - 1] == 0:
                hits.append(start + at - raw)
            position = at + 1
    return hits


def string_refs(image, text):
    """The function that references a unique NUL-terminated ASCII string with lea reg, [rip+disp32]."""
    hits = string_hits(image, text)
    if len(hits) != 1:
        return None, f"string found {len(hits)} times"
    functions = set()
    for _, start, length, raw, flags in image.sections:
        if not flags & EXECUTE:
            continue
        for match in LEA_RIP.finditer(image.data, raw, raw + length - 7):
            at = match.start()
            if start + at - raw + 7 + struct.unpack_from("<i", image.data, at + 3)[0] == hits[0]:
                functions.add(image.function(start + at - raw))
    if len(functions) != 1 or None in functions:
        return None, f"string referenced from {len(functions)} functions"
    return functions.pop()[0], "string reference"


def capture_problem(image, start, capture):
    """Why a capture does not sit where its kind says, or None. The bridge decodes the instruction with Frida's
    Instruction.parse; offline, the ModRM/opcode byte in front of the field is checked instead."""
    at, size, kind = start + capture["at"], capture["size"], capture["kind"]
    before = image.read(at - 2, 2)
    if before is None:
        return "outside the image"
    if kind == "rel32":
        ok = size == 4 and capture.get("end") == capture["at"] + 4 and \
            (before[1] in (0xE8, 0xE9) or (before[0] == 0x0F and 0x80 <= before[1] <= 0x8F))
    elif kind == "rip":
        ok = size == 4 and before[1] & 0xC7 == 0x05
    elif kind == "disp":
        mod = {1: 0x40, 4: 0x80}.get(size)
        # ModRM right before the displacement, or ModRM (rm=100) + SIB.
        ok = mod is not None and ((before[1] & 0xC0 == mod and before[1] & 7 != 4) or
                                  (before[0] & 0xC0 == mod and before[0] & 7 == 4))
    else:
        ok = kind == "imm" and size in (1, 2, 4)
    return None if ok else f"{kind} capture at +{capture['at']} does not follow a matching opcode/ModRM byte"


def capture_value(image, start, capture):
    at, size, kind = capture["at"], capture["size"], capture["kind"]
    raw = image.read(start + at, size)
    if kind in ("rip", "rel32"):
        return image.base + start + capture.get("end", at + size) + int.from_bytes(raw, "little", signed=True)
    return int.from_bytes(raw, "little", signed=kind == "disp")


def parse_int(value):
    return int(value, 0) if isinstance(value, str) else int(value)


def pick_build(signatures, image, sha256=None):
    """(key of the pinned build to use, why): the one with this SHA-256, else the one whose pinned symbols the
    image matches best (Game.patched.exe differs from the official file only in the login patch), else None."""
    builds, specs = signatures["builds"], signatures["symbols"]
    if sha256 in builds:
        return sha256, "SHA-256"
    best, hits = None, 0
    for key, build in builds.items():
        base = parse_int(build["imageBase"])
        count = sum(1 for name, va in build["symbols"].items() if name in specs and
                    matches_at(image, parse_int(va) - base - specs[name].get("offset", 0), specs[name]["pattern"]))
        if count > hits:
            best, hits = key, count
    return (best, f"{hits} of {len(specs)} pinned symbols match") if best else (None, "no listed build matches")


def latest_build(signatures):
    """The last listed build: where constants come from when the image matches no build."""
    return list(signatures["builds"])[-1] if signatures["builds"] else None


def resolve(signatures, image, mode="pinned", sha256=None):
    """{build, symbols, globals, offsets} with, per entry, rva/value, method (pinned/signature/xref/string/constant/
    unresolved) and detail. mode: pinned (normal), signature (skip pinned), xref (also prefer cross-references).
    Offsets derived from code whose sources are all unresolved fall back to the pinned value, marked unverified."""
    specs = signatures["symbols"]
    build_key, build_reason = pick_build(signatures, image, sha256)
    pins = signatures["builds"][build_key] if build_key else None
    constants = signatures["builds"].get(build_key or latest_build(signatures), {})
    symbols = {}

    def location_ok(name, start):
        check = specs[name].get("start")
        if not check or image.pdata is None:
            return True, ""
        if check == "function":
            function = image.function(start)
            return (function is not None and function[0] == start), "not a function start"
        if check.startswith("inside:"):
            parent = symbols.get(check[7:])
            if not parent or parent["rva"] is None:
                return True, f"{check[7:]} unresolved; not checked"
            mine, theirs = image.function(start), image.function(parent["start"])
            return (mine is not None and mine == theirs), f"not inside {check[7:]}"
        raise ValueError(f"{name}: unknown start check {check!r}")

    def accept(name, start, method, detail=""):
        spec = specs[name]
        ok, why = location_ok(name, start)
        if not ok:
            return False, why
        details = [detail] if detail else []
        captures = {}
        if matches_at(image, start, spec["pattern"]):
            for cap, capture in spec.get("captures", {}).items():
                problem = capture_problem(image, start, capture)
                if problem:
                    details.append(f"{cap}: {problem}")
                else:
                    captures[cap] = capture_value(image, start, capture)
        elif method in ("signature", "pinned"):
            return False, "pattern mismatch"
        else:
            details.append("pattern differs here, captures unavailable")
        if why and why.endswith("not checked"):
            details.append(why)
        symbols[name] = {"rva": start + spec.get("offset", 0), "start": start, "method": method,
                         "detail": "; ".join(details), "captures": captures}
        return True, ""

    def has_xref(name):
        return bool(specs[name].get("callers") or specs[name].get("string"))

    def try_pinned(name):
        va = (pins or {}).get("symbols", {}).get(name)
        if va is None:
            return False, "no pinned address"
        start = parse_int(va) - parse_int(pins["imageBase"]) - specs[name].get("offset", 0)
        if not matches_at(image, start, specs[name]["pattern"]):
            return False, "pinned address does not match"
        return accept(name, start, "pinned")

    def try_signature(name):
        spec = specs[name]
        check = spec.get("start", "")
        ranges = image.code_ranges()
        parent = symbols.get(check[7:]) if check.startswith("inside:") else None
        if parent and parent["rva"] is not None:
            # A function's chained fragments usually follow it; location_ok() checks the match belongs to it.
            ranges = [(parent["start"], INSIDE_WINDOW)]
        found = scan(image, spec["pattern"], ranges)
        if len(found) != 1:
            return False, f"signature matched {len(found)} times"
        return accept(name, found[0], "signature")

    def try_xref(name):
        spec = specs[name]
        targets = {}
        for source in spec.get("callers", []):
            caller, cap = source.split(".")
            value = symbols.get(caller, {}).get("captures", {}).get(cap)
            if value is not None:
                targets.setdefault(value - image.base, []).append(caller)
        if len(targets) > 1:
            return False, "callers disagree: " + ", ".join(f"{image.base + t:#x}" for t in targets)
        if targets:
            (target, callers), = targets.items()
            return accept(name, target - spec.get("offset", 0), "xref", "call target in " + ", ".join(callers))
        if spec.get("string"):
            begin, why = string_refs(image, spec["string"])
            if begin is None:
                return False, why
            return accept(name, begin, "string", f"references \"{spec['string']}\"")
        return False, "no resolved caller"

    reasons = {name: [] for name in specs}
    deferred = []

    def note(name, why):
        if why and why not in reasons[name]:
            reasons[name].append(why)

    def parent_pending(name):
        check = specs[name].get("start", "")
        return check.startswith("inside:") and check[7:] not in symbols

    def follow():
        progress = True
        while progress:
            progress = False
            for name in specs:
                if name not in symbols and has_xref(name):
                    ok, why = try_xref(name)
                    progress |= ok
                    if not ok:
                        note(name, why)

    for name in specs:
        if mode == "pinned":
            ok, why = try_pinned(name)
            if ok:
                continue
            note(name, why)
        if (mode == "xref" and has_xref(name)) or parent_pending(name):
            deferred.append(name)
            continue
        ok, why = try_signature(name)
        if not ok:
            note(name, why)
    follow()
    # Signatures deferred for a cross-reference or for their parent function, then what they make reachable.
    for name in deferred:
        if name not in symbols:
            ok, why = try_signature(name)
            if not ok:
                note(name, why)
    follow()
    for name in specs:
        symbols.setdefault(name, {"rva": None, "start": None, "method": "unresolved",
                                  "detail": "; ".join(reasons[name]), "captures": {}})

    def source_values(sources):
        values, missing = {}, []
        for source in sources:
            name, cap = source.split(".")
            value = symbols[name]["captures"].get(cap)
            (missing.append(source) if value is None else values.setdefault(source, value))
        return values, missing

    globals_ = {}
    for name, spec in signatures.get("globals", {}).items():
        values, missing = source_values([spec["from"]])
        if values:
            (source, value), = values.items()
            globals_[name] = {"rva": value - image.base, "method": symbols[source.split(".")[0]]["method"],
                              "detail": f"from {source}"}
        else:
            globals_[name] = {"rva": None, "method": "unresolved", "detail": f"{spec['from']} unresolved"}
    offsets = {}
    for name, spec in signatures.get("offsets", {}).items():
        sources = spec.get("from", [])
        sources = [sources] if isinstance(sources, str) else sources
        values, missing = source_values(sources)
        adjusted = {source: value + spec.get("add", 0) for source, value in values.items()}
        distinct = sorted(set(adjusted.values()))
        pinned = constants.get("offsets", {}).get(name)
        if len(distinct) > 1:
            offsets[name] = {"value": None, "method": "unresolved", "unverified": False,
                             "detail": "sources disagree: " + ", ".join(f"{s}={v:#x}" for s, v in adjusted.items())}
        elif distinct:
            first = next(iter(adjusted))
            offsets[name] = {"value": distinct[0], "method": symbols[first.split(".")[0]]["method"],
                             "unverified": False, "detail": "from " + ", ".join(adjusted)}
        elif pinned is None:
            offsets[name] = {"value": None, "method": "unresolved", "unverified": False,
                             "detail": "no pinned value" + (", sources unresolved" if sources else "")}
        else:
            offsets[name] = {"value": parse_int(pinned), "method": "constant", "unverified": bool(sources),
                             "detail": ("unverified: sources unresolved: " + ", ".join(missing)) if sources
                             else "pinned constant"}
    return {"build": build_key, "buildReason": build_reason, "symbols": symbols, "globals": globals_,
            "offsets": offsets}


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def build_label(data):
    """The client's own build label from its `/tags/<label>` version string (e.g. 3.29.3b); the executable has no
    version resource. `data` is the file or the mapped image."""
    match = re.search(rb"/tags/([0-9][0-9A-Za-z._-]{0,31})\0", data)
    return match.group(1).decode("ascii") if match else None


def build_entry(signatures, image, result, version=None):
    """The pinned values of this image: what a new `builds` entry holds. Constants and unresolved offsets keep the
    reference build's values; unresolved symbols and globals get no pin."""
    reference = signatures["builds"].get(result["build"] or latest_build(signatures), {})
    return {
        "version": version or (reference.get("version") if result["buildReason"] == "SHA-256" else None) or "unknown",
        "label": image.build_label() or "unknown",
        "imageBase": f"{image.base:#x}",
        "symbols": {name: f"{image.base + found['rva']:#x}" for name, found in result["symbols"].items()
                    if found["rva"] is not None},
        "globals": {name: f"{image.base + found['rva']:#x}" for name, found in result["globals"].items()
                    if found["rva"] is not None},
        "offsets": {name: f"{found['value']:#x}" if found["value"] is not None and not found["unverified"]
                    else reference.get("offsets", {}).get(name, "0x0")
                    for name, found in result["offsets"].items()},
    }


def updated(signatures, image, result, sha256, version=None):
    """A copy of the signatures with this image's build added (or replaced, for the same SHA-256)."""
    out = json.loads(json.dumps(signatures))
    out["builds"][sha256] = build_entry(signatures, image, result, version)
    return out


def unresolved(result):
    names = [f"symbol {n}" for n, r in result["symbols"].items() if r["rva"] is None]
    names += [f"symbol {n} ({r['detail']})" for n, r in result["symbols"].items()
              if r["rva"] is not None and ("pattern differs" in r["detail"] or "capture" in r["detail"])]
    names += [f"global {n}" for n, r in result["globals"].items() if r["rva"] is None]
    names += [f"offset {n}" for n, r in result["offsets"].items() if r["value"] is None or r["unverified"]]
    return names


def report(signatures, result, sha256, entry, path):
    reference_key = result["build"] or latest_build(signatures)
    reference = signatures["builds"].get(reference_key, {})
    lines = [f"Client `{path.name}`: version {entry['version']}, build {entry['label']}, SHA-256 `{sha256}`.",
             f"Compared with the pinned build {reference.get('version', '-')} ({reference.get('label', '-')}, "
             f"`{(reference_key or '-')[:16]}…`, chosen by {result['buildReason']}).", ""]
    missing = unresolved(result)
    lines.append(f"**{len(missing)} unresolved:** " + ", ".join(missing) if missing else "Everything resolved.")

    def table(kind, rows, old_values):
        out = ["", f"| {kind} | old | new | method | status |", "|---|---|---|---|---|"]
        for name, new, method, detail, bad in rows:
            old = old_values.get(name, "-")
            status = "unresolved" if new is None else "unverified" if bad else \
                "unchanged" if old != "-" and parse_int(old) == parse_int(new) else "changed"
            out.append(f"| {name} | {old} | {new or '-'} | {method}{f' ({detail})' if detail else ''} | {status} |")
        return out

    def va(rva):
        return f"{parse_int(entry['imageBase']) + rva:#x}" if rva is not None else None

    lines += table("symbol", [(n, va(r["rva"]), r["method"], r["detail"], False)
                              for n, r in result["symbols"].items()], reference.get("symbols", {}))
    lines += table("global", [(n, va(r["rva"]), r["method"], r["detail"], False)
                              for n, r in result["globals"].items()], reference.get("globals", {}))
    lines += table("offset", [(n, f"{r['value']:#x}" if r["value"] is not None else None, r["method"], r["detail"],
                               r["unverified"]) for n, r in result["offsets"].items()], reference.get("offsets", {}))
    return "\n".join(lines) + "\n"


def check(signatures, result):
    """Problems found when the result must reproduce the chosen build's pinned values exactly."""
    if result["build"] is None:
        return ["no listed build matches this executable"]
    build = signatures["builds"][result["build"]]
    base = parse_int(build["imageBase"])
    problems = []
    for name, found in result["symbols"].items():
        pinned = build["symbols"].get(name)
        if found["rva"] is None:
            problems.append(f"{name}: unresolved ({found['detail']})")
        elif pinned is None or base + found["rva"] != parse_int(pinned):
            problems.append(f"{name}: {found['method']} found {base + found['rva']:#x}, pinned {pinned}")
        elif found["detail"] and found["method"] in ("pinned", "signature"):
            problems.append(f"{name}: {found['detail']}")
        elif len(found["captures"]) != len(signatures["symbols"][name].get("captures", {})):
            problems.append(f"{name}: captures unavailable")
    for name, found in result["globals"].items():
        pinned = build["globals"].get(name)
        if found["rva"] is None or pinned is None or base + found["rva"] != parse_int(pinned):
            problems.append(f"global {name}: {found['detail']}")
    for name, found in result["offsets"].items():
        pinned = build["offsets"].get(name)
        if pinned is None or found["value"] != parse_int(pinned) or found["unverified"]:
            problems.append(f"offset {name}: {found['value']} vs pinned {pinned} ({found['detail']})")
    return problems


# Exit status of a run without --check: distinct from Python's own 1 (an uncaught exception) and 2 (usage).
UNCHANGED, UPDATED, PARTIAL = 0, 10, 20


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("exe", type=Path, nargs="+", help="client executable(s); several only with --check")
    parser.add_argument("--signatures", type=Path, default=SIGNATURES)
    parser.add_argument("--mode", choices=("pinned", "signature", "xref"), default=None,
                        help="resolution order (default: pinned; --check defaults to signature)")
    parser.add_argument("--write", action="store_true", help="add this build to the signatures file")
    parser.add_argument("--version", help="client patch version to record (e.g. 3.29.4.1)")
    parser.add_argument("--report", type=Path, help="write the markdown report here")
    parser.add_argument("--check", action="store_true", help="require the matching build's pins to be reproduced")
    options = parser.parse_args()
    signatures = json.loads(options.signatures.read_text(encoding="utf-8"))
    mode = options.mode or ("signature" if options.check else "pinned")
    if options.check:
        failed = 0
        for exe in options.exe:
            image = Image.open(exe)
            result = resolve(signatures, image, mode, sha256_file(exe))
            for name, found in result["symbols"].items():
                where = f"{image.base + found['rva']:#x}" if found["rva"] is not None else "-"
                print(f"{name:<20}  {where:>12}  {found['method']}" + (f" ({found['detail']})" if found["detail"] else ""))
            for name, found in result["globals"].items():
                where = f"{image.base + found['rva']:#x}" if found["rva"] is not None else "-"
                print(f"global {name}: {where} {found['method']} ({found['detail']})")
            for name, found in result["offsets"].items():
                value = f"{found['value']:#x}" if found["value"] is not None else "-"
                print(f"offset {name}: {value} {found['method']} ({found['detail']})")
            problems = check(signatures, result)
            build = signatures["builds"].get(result["build"] or "", {})
            print("\n".join(problems) if problems else
                  f"OK: {exe.name} is build {build.get('version')} ({build.get('label')}, by {result['buildReason']}): "
                  f"{len(result['symbols'])} symbols ({mode}), {len(result['globals'])} globals and "
                  f"{len(result['offsets'])} offsets reproduce its pinned values")
            failed += bool(problems)
        return 1 if failed else 0
    if len(options.exe) != 1:
        parser.error("give one executable unless --check")
    exe = options.exe[0]
    image = Image.open(exe)
    sha256 = sha256_file(exe)  # streamed: hashing the mmap would keep all 79 MB resident
    result = resolve(signatures, image, mode, sha256)
    new = updated(signatures, image, result, sha256, options.version)
    text = report(signatures, result, sha256, new["builds"][sha256], exe)
    if options.report:
        options.report.write_text(text, encoding="utf-8")
    print(text)
    if options.write:
        options.signatures.write_text(json.dumps(new, indent=2) + "\n", encoding="utf-8")
    if unresolved(result):
        return PARTIAL
    return UNCHANGED if new == signatures else UPDATED


if __name__ == "__main__":
    sys.exit(main())
