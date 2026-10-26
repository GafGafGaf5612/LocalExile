"""Draft a wildcarded signature entry for scripts/dev/client-signatures.json (authoring aid; needs capstone + pefile).

    .venv/Scripts/python.exe scripts/re/signature_pattern.py 0x14213ea10 0x14213ea94
    .venv/Scripts/python.exe scripts/re/signature_pattern.py 0x14213ea10 +0x111 --capture 0x14213eafc:socket \
        --capture 0x14213eb08:loginWriter:rel32

Disassembles [start, end) of Client/Game.exe and wildcards the bytes a patch is likely to move: every branch
displacement (rel8 and rel32), every RIP-relative disp32, and stack-frame sizes (`sub rsp, N`, `lea rbp, [rsp+N]`).
Each `--capture VA:NAME[:KIND]` also wildcards that instruction's struct displacement (`disp`, default; `rip` when
RIP-relative), immediate (`imm`) or call target (`rel32`) and records where it is, so the bridge and
scripts/ci/resolve_client.py can read the value back from whatever build they find. `--wild VA` wildcards one
instruction's displacement/immediate without capturing it, `--keep VA` keeps it literal.

Prints the disassembly, the pattern, how many times it occurs in the executable sections (a signature must occur
exactly once), and the JSON entry (`pattern`, `captures`) to paste into client-signatures.json `symbols`;
the start VA goes into the current build's `symbols` pins (plus `offset` for a hook inside the range).
"""
import argparse
import json
from pathlib import Path
import re
import sys

import pefile
from capstone import CS_ARCH_X86, CS_GRP_CALL, CS_GRP_JUMP, CS_MODE_64, CS_OP_IMM, Cs

ROOT = Path(__file__).resolve().parents[2]


def draft(pe, start, end, captures=None, wild=(), keep=()):
    """The signature entry for [start, end): (entry, {capture: current value}, disassembly lines)."""
    base = pe.OPTIONAL_HEADER.ImageBase
    code = pe.get_data(start - base, end - start)
    captures = captures or {}
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    mask = [True] * len(code)
    found, values, lines = {}, {}, []
    position = 0
    for insn in md.disasm(code, start):
        offset = insn.address - start
        position = offset + insn.size
        fields = []
        immediate = (insn.imm_offset, insn.imm_size)
        displacement = (insn.disp_offset, insn.disp_size)
        rip = "rip" in insn.op_str
        branch = (insn.group(CS_GRP_JUMP) or insn.group(CS_GRP_CALL)) and insn.imm_size and \
            insn.operands and insn.operands[0].type == CS_OP_IMM
        if insn.address in captures:
            name, kind = captures[insn.address]
            field = immediate if kind in ("imm", "rel32") else displacement
            if not field[1]:
                raise ValueError(f"{insn.address:#x} {insn.mnemonic} {insn.op_str} has no {kind} field")
            fields.append(field)
            raw = code[offset + field[0]:offset + field[0] + field[1]]
            value = int.from_bytes(raw, "little", signed=kind != "imm")
            if kind == "disp" and rip:
                kind = "rip"
            entry = {"at": offset + field[0], "size": field[1], "kind": kind, "insn": offset}
            if kind in ("rip", "rel32"):
                entry["end"] = offset + insn.size
                value += insn.address + insn.size
            found[name] = entry
            values[name] = value
        elif insn.address in keep:
            pass
        elif insn.address in wild:
            fields.append(displacement if displacement[1] else immediate)
        elif branch:
            fields.append(immediate)
        elif rip and insn.disp_size:
            fields.append(displacement)
        elif insn.mnemonic == "sub" and insn.op_str.startswith("rsp, ") and insn.imm_size:
            fields.append(immediate)
        elif insn.mnemonic == "lea" and "[rsp" in insn.op_str and insn.disp_size:
            fields.append(displacement)
        for field_offset, size in fields:
            for k in range(size):
                mask[offset + field_offset + k] = False
        note = ""
        if insn.group(CS_GRP_CALL) and insn.imm_size == 4 and branch:
            note = f"   ; call {insn.operands[0].imm:#x}"
        elif rip and insn.disp_size:
            disp = int.from_bytes(code[offset + insn.disp_offset:offset + insn.disp_offset + 4], "little", signed=True)
            note = f"   ; -> {insn.address + insn.size + disp:#x}"
        lines.append(f"{offset:4x} {insn.address:x}: {insn.bytes.hex():<24} {insn.mnemonic} {insn.op_str}{note}")
    if position != len(code):
        raise ValueError(f"{end:#x} is not an instruction boundary (decoded {position:#x} of {len(code):#x} bytes)")
    pattern = " ".join(f"{b:02x}" if literal else "??" for b, literal in zip(code, mask))
    entry = {"pattern": pattern}
    if found:
        entry["captures"] = found
    return entry, values, lines


def matches(pe, pattern):
    """Image VAs where the pattern occurs in the executable sections."""
    regex = re.compile(b"".join(b"." if token == "??" else re.escape(bytes.fromhex(token))
                                for token in pattern.split()), re.S)
    base, data, found = pe.OPTIONAL_HEADER.ImageBase, pe.__data__, []
    for section in pe.sections:
        if section.Characteristics & 0x20000000:
            raw = data[section.PointerToRawData:section.PointerToRawData + section.SizeOfRawData]
            found += [base + section.VirtualAddress + m.start() for m in regex.finditer(raw)]
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("start", help="first instruction (image VA)")
    parser.add_argument("end", help="end VA (exclusive) or +length")
    parser.add_argument("--capture", action="append", default=[], metavar="VA:NAME[:KIND]")
    parser.add_argument("--wild", action="append", default=[], metavar="VA")
    parser.add_argument("--keep", action="append", default=[], metavar="VA")
    parser.add_argument("--exe", default=str(ROOT / "Client" / "Game.exe"))
    options = parser.parse_args()
    pe = pefile.PE(options.exe, fast_load=True)
    start = int(options.start, 16)
    end = start + int(options.end[1:], 16) if options.end.startswith("+") else int(options.end, 16)
    captures = {}
    for spec in options.capture:
        va, name, *kind = spec.split(":")
        captures[int(va, 16)] = (name, kind[0] if kind else "disp")
    try:
        entry, values, lines = draft(pe, start, end, captures, {int(v, 16) for v in options.wild},
                                     {int(v, 16) for v in options.keep})
    except ValueError as error:
        sys.exit(str(error))
    print("\n".join(lines))
    found = matches(pe, entry["pattern"])
    print(f"\n{len(found)} match(es) in executable sections" + (": " + ", ".join(f"{m:#x}" for m in found[:8])
                                                                 if len(found) != 1 else ""))
    for name, value in values.items():
        print(f"capture {name} = {value:#x}")
    print(f"pinned VA (for builds.<sha256>.symbols): {start:#x}")
    print(json.dumps(entry, indent=2))
    return 0 if len(found) == 1 else 1


if __name__ == "__main__":
    sys.exit(main())
