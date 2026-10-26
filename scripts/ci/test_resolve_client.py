"""resolve_client.py on small synthetic PE images: no client needed (python -I -m unittest discover -s scripts/ci)."""
import contextlib
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest

import resolve_client as rc

BASE = 0x140000000
TEXT, RDATA, DATA, PDATA = 0x1000, 0x3000, 0x4000, 0x5000
STRING = b"Callee marker string"

# caller: push rbp; mov rbp,rsp; call callee; mov rcx,[rip+global]; mov eax,[rcx+0x358]; lea rdx,[rip+string]; pop rbp; ret
CALLER = "55 48 8b ec e8 ?? ?? ?? ?? 48 8b 0d ?? ?? ?? ?? 8b 81 ?? ?? ?? ?? 48 8d 15 ?? ?? ?? ?? 5d c3"
# callee: sub rsp,0x28; mov eax,0x2a; lea rcx,[rip+string]; add rsp,0x28; ret
CALLEE = "48 83 ec 28 b8 2a 00 00 00 48 8d 0d ?? ?? ?? ?? 48 83 c4 28 c3"


def caller_code(at, callee, global_va, field, string):
    """CALLER placed at text offset `at`, its rel32/disp32 pointing at the given RVAs."""
    code = bytearray.fromhex("55488bec")
    code += b"\xe8" + struct.pack("<i", callee - (TEXT + at + 9))
    code += b"\x48\x8b\x0d" + struct.pack("<i", global_va - (TEXT + at + 16))
    code += b"\x8b\x81" + struct.pack("<i", field)
    code += b"\x48\x8d\x15" + struct.pack("<i", string - (TEXT + at + 29))
    return bytes(code + b"\x5d\xc3")


def callee_code(at, string, value=0x2A):
    code = bytearray.fromhex("4883ec28") + b"\xb8" + struct.pack("<I", value)
    code += b"\x48\x8d\x0d" + struct.pack("<i", string - (TEXT + at + 16))
    return bytes(code + bytes.fromhex("4883c428c3"))


def build_pe(text, functions, rdata=b"", chained=()):
    """A minimal PE32+ file: .text (code), .rdata, .data and .pdata with one RUNTIME_FUNCTION per (begin, end) text
    offset; `chained` lists (begin, end, primary index) fragments whose unwind info chains to that function."""
    unwind = PDATA + 0x800
    pdata = bytearray()
    unwinds = bytearray()
    entries = [(TEXT + b, TEXT + e, unwind) for b, e in functions]
    for begin, end, primary in chained:
        info = unwind + 16 + len(unwinds)
        unwinds += bytes([1 | (4 << 3), 0, 0, 0]) + struct.pack("<III", *entries[primary])
        entries.append((TEXT + begin, TEXT + end, info))
    for entry in sorted(entries):
        pdata += struct.pack("<III", *entry)
    pdata = pdata.ljust(0x800, b"\0") + bytes([1, 0, 0, 0]).ljust(16, b"\0") + unwinds
    sections = [(b".text", TEXT, text, 0x60000020), (b".rdata", RDATA, rdata, 0x40000040),
                (b".data", DATA, bytes(0x100), 0xC0000040), (b".pdata", PDATA, bytes(pdata), 0x40000040)]
    header = bytearray(0x400)
    header[:2] = b"MZ"
    struct.pack_into("<I", header, 0x3C, 0x80)
    header[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HH", header, 0x84, 0x8664, len(sections))
    struct.pack_into("<H", header, 0x94, 0xF0)
    optional = 0x98
    struct.pack_into("<H", header, optional, 0x20B)
    struct.pack_into("<Q", header, optional + 24, BASE)
    struct.pack_into("<I", header, optional + 56, 0x8000)
    struct.pack_into("<I", header, optional + 108, 16)
    struct.pack_into("<II", header, optional + 112 + 24, PDATA, len(entries) * 12)
    raw = 0x400
    body = bytearray()
    for i, (name, rva, content, flags) in enumerate(sections):
        size = (len(content) + 0x1FF) & ~0x1FF or 0x200
        struct.pack_into("<8sIIII", header, optional + 0xF0 + 40 * i, name, max(len(content), 1), rva, size, raw)
        struct.pack_into("<I", header, optional + 0xF0 + 40 * i + 36, flags)
        body += content.ljust(size, b"\0")
        raw += size
    return bytes(header + body)


def text_with(parts, size=0x1800):
    text = bytearray(b"\xcc" * size)
    for at, code in parts:
        text[at:at + len(code)] = code
    return bytes(text)


def scenario(caller_at=0x100, callee_at=0x400, global_va=DATA + 0x40, field=0x358, extra=(), chained=()):
    rdata = bytes(0x20) + STRING + b"\0"
    string = RDATA + 0x20
    parts = [(caller_at, caller_code(caller_at, TEXT + callee_at, global_va, field, string)),
             (callee_at, callee_code(callee_at, string))] + list(extra)
    functions = [(caller_at, caller_at + 0x40), (callee_at, callee_at + 0x40)]
    functions += [(at, at + 0x40) for at, _ in extra]
    return build_pe(text_with(parts), functions, rdata, chained), string


def signatures(image_bytes):
    """Signatures pinned to scenario() at its defaults."""
    image = rc.Image(image_bytes)
    caller, callee = TEXT + 0x100, TEXT + 0x400
    return {
        "client": {"version": "1.0.0.0", "build": "test", "sha256": "0" * 64, "imageBase": f"{BASE:#x}"},
        "symbols": {
            "caller": {"va": f"{BASE + caller:#x}", "start": "function", "pattern": CALLER,
                       "bytes": image.read(caller, 31).hex(),
                       "captures": {"callee": {"at": 5, "size": 4, "kind": "rel32", "insn": 4, "end": 9},
                                    "global": {"at": 12, "size": 4, "kind": "rip", "insn": 9, "end": 16},
                                    "field": {"at": 18, "size": 4, "kind": "disp", "insn": 16}}},
            "callee": {"va": f"{BASE + callee:#x}", "start": "function", "callers": ["caller.callee"],
                       "string": STRING.decode(), "pattern": CALLEE,
                       "bytes": image.read(callee, 21).hex(),
                       "captures": {"value": {"at": 5, "size": 4, "kind": "imm", "insn": 4}}},
            "tail": {"va": f"{BASE + caller + 16:#x}", "start": "inside:caller", "pattern": "8b 81 ?? ?? ?? ?? 48 8d 15",
                     "bytes": image.read(caller + 16, 9).hex()},
        },
        "globals": {"g": {"va": f"{BASE + DATA + 0x40:#x}", "from": "caller.global"}},
        "offsets": {"field": {"value": "0x358", "from": ["caller.field"]}, "value": {"value": "0x2a", "from": "callee.value"},
                    "fixed": {"value": "0x10"}},
    }


class ResolveTests(unittest.TestCase):
    def setUp(self):
        self.original, _ = scenario()
        self.signatures = signatures(self.original)

    def resolve(self, data, mode="pinned"):
        return rc.resolve(self.signatures, rc.Image(data), mode)

    def test_unchanged_client_resolves_pinned_and_signature_paths_identically(self):
        for mode in ("pinned", "signature", "xref"):
            result = self.resolve(self.original, mode)
            self.assertEqual(rc.check(self.signatures, result), [], mode)
        pinned = self.resolve(self.original)
        self.assertEqual({s["method"] for s in pinned["symbols"].values()}, {"pinned"})
        self.assertEqual(pinned["offsets"]["field"]["value"], 0x358)
        self.assertEqual(pinned["offsets"]["fixed"]["method"], "constant")
        self.assertEqual(self.resolve(self.original, "xref")["symbols"]["callee"]["method"], "xref")

    def test_shifted_code_is_found_by_signature_with_the_same_globals(self):
        moved, _ = scenario(caller_at=0x340, callee_at=0x900)
        result = self.resolve(moved)
        self.assertEqual(result["symbols"]["caller"]["method"], "signature")
        self.assertEqual(result["symbols"]["caller"]["rva"], TEXT + 0x340)
        self.assertEqual(result["symbols"]["callee"]["rva"], TEXT + 0x900)
        self.assertEqual(result["symbols"]["tail"]["rva"], TEXT + 0x340 + 16)
        self.assertEqual(result["globals"]["g"]["rva"], DATA + 0x40)
        self.assertEqual(rc.unresolved(result), [])
        new = rc.updated(self.signatures, rc.Image(moved), result, "1" * 64, "1.0.0.1")
        self.assertEqual(new["symbols"]["caller"]["va"], f"{BASE + TEXT + 0x340:#x}")
        self.assertEqual(new["symbols"]["caller"]["bytes"], rc.Image(moved).read(TEXT + 0x340, 31).hex())
        self.assertEqual(new["client"]["version"], "1.0.0.1")
        # The updated file pins the new build: everything is pinned again.
        again = rc.resolve(new, rc.Image(moved))
        self.assertEqual({s["method"] for s in again["symbols"].values()}, {"pinned"})

    def test_changed_displacements_move_globals_and_offsets(self):
        changed, _ = scenario(global_va=DATA + 0x80, field=0x360)
        result = self.resolve(changed)
        self.assertEqual(result["symbols"]["caller"]["method"], "signature")
        self.assertEqual(result["globals"]["g"]["rva"], DATA + 0x80)
        self.assertEqual(result["offsets"]["field"]["value"], 0x360)
        new = rc.updated(self.signatures, rc.Image(changed), result, "1" * 64)
        self.assertEqual(new["globals"]["g"]["va"], f"{BASE + DATA + 0x80:#x}")
        self.assertEqual(new["offsets"]["field"]["value"], "0x360")
        self.assertEqual(new["offsets"]["fixed"]["value"], "0x10")

    def test_ambiguous_signature_is_refused(self):
        string = RDATA + 0x20
        duplicate = (0x800, caller_code(0x800, TEXT + 0x400, DATA + 0x40, 0x358, string))
        twice, _ = scenario(extra=[duplicate])
        result = self.resolve(twice, "signature")
        self.assertIsNone(result["symbols"]["caller"]["rva"])
        self.assertIn("matched 2 times", result["symbols"]["caller"]["detail"])
        self.assertIsNone(result["globals"]["g"]["rva"])
        self.assertEqual(result["offsets"]["field"]["method"], "constant")
        self.assertTrue(result["offsets"]["field"]["detail"].startswith("sources unresolved"))
        self.assertIn("symbol caller", rc.unresolved(result))
        # The callee is still unique; it does not depend on the caller.
        self.assertEqual(result["symbols"]["callee"]["method"], "signature")

    def test_ambiguous_callee_is_resolved_through_its_caller(self):
        string = RDATA + 0x20
        twice, _ = scenario(extra=[(0xC00, callee_code(0xC00, string))])
        result = self.resolve(twice, "signature")
        self.assertEqual(result["symbols"]["callee"]["method"], "xref")
        self.assertEqual(result["symbols"]["callee"]["rva"], TEXT + 0x400)
        self.assertEqual(result["offsets"]["value"]["value"], 0x2A)

    def test_string_anchor_finds_a_rewritten_function(self):
        string = RDATA + 0x20
        # The callee's body changed (different immediate encoding) and the caller is gone.
        rewritten = bytes.fromhex("4883ec28") + b"\x31\xc0\x90\x90\x90" + b"\x48\x8d\x0d" + \
            struct.pack("<i", string - (TEXT + 0x400 + 16)) + bytes.fromhex("4883c428c3")
        data = build_pe(text_with([(0x400, rewritten)]), [(0x400, 0x440)], bytes(0x20) + STRING + b"\0")
        result = self.resolve(data)
        callee = result["symbols"]["callee"]
        self.assertEqual((callee["method"], callee["rva"]), ("string", TEXT + 0x400))
        self.assertIn("pattern differs", callee["detail"])
        self.assertIn("symbol callee (pattern differs)", rc.unresolved(result))
        self.assertEqual(result["offsets"]["value"]["method"], "constant")

    def test_match_must_be_a_function_start(self):
        # Same bytes, but .pdata says the function begins 0x10 earlier: not the function we want.
        data, _ = scenario()
        at, count = rc.Image(data).pdata
        data = bytearray(data)
        for entry in range(at, at + 12 * count, 12):
            if struct.unpack_from("<I", data, entry)[0] == TEXT + 0x100:
                struct.pack_into("<I", data, entry, TEXT + 0xF0)
        result = rc.resolve(self.signatures, rc.Image(data), "signature")
        self.assertIsNone(result["symbols"]["caller"]["rva"])
        self.assertIn("not a function start", result["symbols"]["caller"]["detail"])

    def test_chained_unwind_info_maps_fragments_to_their_function(self):
        data, _ = scenario(chained=[(0x1000, 0x1040, 0)])
        image = rc.Image(data)
        self.assertEqual(image.function(TEXT + 0x1010), (TEXT + 0x100, TEXT + 0x140))
        self.assertEqual(image.function(TEXT + 0x410), (TEXT + 0x400, TEXT + 0x440))
        self.assertIsNone(image.function(TEXT + 0x1500))

    def test_build_label(self):
        self.assertEqual(rc.build_label(b"xx PoE \0/tags/3.29.3b\0\0"), "3.29.3b")
        self.assertIsNone(rc.build_label(b"no label"))


class MainTests(unittest.TestCase):
    def run_main(self, exe_bytes, signatures, *args):
        with tempfile.TemporaryDirectory() as folder:
            exe, sig, report = Path(folder) / "PathOfExile.exe", Path(folder) / "sig.json", Path(folder) / "r.md"
            exe.write_bytes(exe_bytes)
            sig.write_text(json.dumps(signatures, indent=2) + "\n", encoding="utf-8")
            argv = sys.argv
            sys.argv = ["resolve_client.py", str(exe), "--signatures", str(sig), "--report", str(report), *args]
            try:
                with contextlib.redirect_stdout(io.StringIO()):
                    code = rc.main()
            finally:
                sys.argv = argv
            text = report.read_text(encoding="utf-8") if report.exists() else None
            return code, json.loads(sig.read_text(encoding="utf-8")), text

    def test_exit_codes_and_write(self):
        original, _ = scenario()
        pinned = signatures(original)
        pinned["client"]["sha256"] = rc.hashlib.sha256(original).hexdigest()
        pinned["client"]["build"] = "unknown"
        code, written, _ = self.run_main(original, pinned, "--write")
        self.assertEqual(code, 0)
        self.assertEqual(written, pinned)
        moved, _ = scenario(caller_at=0x340)
        code, written, report = self.run_main(moved, pinned, "--write", "--version", "1.0.0.1")
        self.assertEqual(code, 1)
        self.assertEqual(written["symbols"]["caller"]["va"], f"{BASE + TEXT + 0x340:#x}")
        self.assertEqual(written["client"]["version"], "1.0.0.1")
        self.assertIn("| caller | 0x140001100 | 0x140001340 | signature | moved |", report)
        string = RDATA + 0x20
        twice, _ = scenario(extra=[(0x800, caller_code(0x800, TEXT + 0x400, DATA + 0x40, 0x358, string))],
                            caller_at=0x340)
        code, written, report = self.run_main(twice, pinned, "--write")
        self.assertEqual(code, 2)
        self.assertEqual(written["symbols"]["caller"]["va"], pinned["symbols"]["caller"]["va"])  # kept
        self.assertIn("unresolved", report)

    def test_check_mode(self):
        original, _ = scenario()
        code, _, _ = self.run_main(original, signatures(original), "--check")
        self.assertEqual(code, 0)
        moved, _ = scenario(caller_at=0x340)
        code, _, _ = self.run_main(moved, signatures(original), "--check")
        self.assertEqual(code, 1)


# The copied client, or (in CI, once everything resolved) the newly downloaded one.
CLIENT = Path(os.environ.get("LOCALEXILE_CLIENT_EXE") or rc.ROOT / "Client" / "Game.exe")


@unittest.skipUnless(CLIENT.exists(), "needs a client executable (Client/Game.exe or LOCALEXILE_CLIENT_EXE)")
class SimulatedPatchTests(unittest.TestCase):
    """The real client with what a patch typically changes: all code moved, struct offsets changed."""

    @classmethod
    def setUpClass(cls):
        cls.signatures = json.loads(rc.SIGNATURES.read_text(encoding="utf-8"))
        cls.data = CLIENT.read_bytes()

    def shifted(self, delta, field_change=0):
        """The client with .text moved up by `delta` bytes (.pdata and chained unwind entries moved with it), the
        captured RIP-relative operands re-aimed at their old targets, and captured struct displacements and
        immediates changed by `field_change`. Other RIP-relative operands now point elsewhere, as after a patch."""
        original = rc.Image(self.data)
        data = bytearray(self.data)
        (text, size, raw), = [(rva, size, raw) for name, rva, size, raw, _ in original.sections if name == ".text"]
        data[raw + delta:raw + size] = data[raw:raw + size - delta]
        data[raw:raw + delta] = b"\xcc" * delta
        at, count = original.pdata
        seen = set()
        for entry in range(at, at + 12 * count, 12):
            begin, end, unwind = struct.unpack_from("<III", data, entry)
            if text <= begin < text + size:
                struct.pack_into("<II", data, entry, begin + delta, end + delta)
            while unwind not in seen:
                info = original.offset(unwind, 4)
                if not data[info] >> 3 & 4:
                    break
                seen.add(unwind)
                chained = original.offset(unwind + 4 + 2 * ((data[info + 2] + 1) & ~1), 12)
                b, e, unwind = struct.unpack_from("<III", data, chained)
                if text <= b < text + size:
                    struct.pack_into("<II", data, chained, b + delta, e + delta)
        for spec in self.signatures["symbols"].values():
            start = int(spec["va"], 16) - BASE - spec.get("offset", 0) + delta
            for capture in spec.get("captures", {}).values():
                field = original.offset(start + capture["at"], capture["size"])
                if capture["kind"] == "rip":
                    struct.pack_into("<i", data, field, struct.unpack_from("<i", data, field)[0] - delta)
                elif capture["kind"] in ("disp", "imm") and field_change:
                    fmt = ({1: "<b", 4: "<i"} if capture["kind"] == "disp" else {1: "<B", 4: "<I"})[capture["size"]]
                    struct.pack_into(fmt, data, field, struct.unpack_from(fmt, data, field)[0] + field_change)
        return rc.Image(bytes(data))

    def test_moved_code_resolves_by_signature_and_cross_reference(self):
        delta = 0x1234
        image = self.shifted(delta)
        for mode in ("pinned", "xref"):
            result = rc.resolve(self.signatures, image, mode)
            for name, spec in self.signatures["symbols"].items():
                found = result["symbols"][name]
                self.assertEqual(found["rva"], int(spec["va"], 16) - BASE + delta, f"{mode} {name}: {found}")
                self.assertNotEqual(found["method"], "pinned")
            for name, spec in self.signatures["globals"].items():
                self.assertEqual(BASE + result["globals"][name]["rva"], int(spec["va"], 16), name)
            for name, spec in self.signatures["offsets"].items():
                self.assertEqual(result["offsets"][name]["value"], int(spec["value"], 16), name)
            self.assertEqual(rc.unresolved(result), [])

    def test_changed_struct_offsets_are_read_from_the_code(self):
        image = self.shifted(0x40, field_change=8)
        result = rc.resolve(self.signatures, image)
        for name, spec in self.signatures["offsets"].items():
            expected = int(spec["value"], 16) + (8 if spec.get("from") else 0)
            self.assertEqual(result["offsets"][name]["value"], expected, name)


class SignatureFileTests(unittest.TestCase):
    """The tracked signatures file is well formed (runs without a client)."""

    def test_entries_are_consistent(self):
        data = json.loads(rc.SIGNATURES.read_text(encoding="utf-8"))
        symbols = data["symbols"]
        for name, spec in symbols.items():
            tokens = spec["pattern"].split()
            rc.compile_pattern(spec["pattern"])
            self.assertEqual(len(spec["bytes"]), 2 * len(tokens), name)
            literal = bytes.fromhex(spec["bytes"])
            for i, token in enumerate(tokens):
                if token != "??":
                    self.assertEqual(int(token, 16), literal[i], f"{name} byte {i}")
            for cap, capture in spec.get("captures", {}).items():
                field = tokens[capture["at"]:capture["at"] + capture["size"]]
                self.assertEqual(field, ["??"] * capture["size"], f"{name}.{cap} must be wildcarded")
                self.assertIn(capture["kind"], ("rel32", "rip", "disp", "imm"))
            for source in spec.get("callers", []):
                caller, cap = source.split(".")
                self.assertEqual(symbols[caller]["captures"][cap]["kind"], "rel32", source)
            check = spec.get("start", "function")
            self.assertTrue(check == "function" or check[7:] in symbols, name)
        for table in ("globals", "offsets"):
            for name, spec in data[table].items():
                sources = spec.get("from", [])
                for source in [sources] if isinstance(sources, str) else sources:
                    symbol, cap = source.split(".")
                    self.assertIn(cap, symbols[symbol]["captures"], f"{table} {name}")


if __name__ == "__main__":
    unittest.main()
