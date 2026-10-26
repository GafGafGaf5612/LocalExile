"""Check the bridge's in-process resolver (client-resolver.js) against the copied client without starting the game.

    .venv/Scripts/python.exe scripts/dev/check_client_signatures.py [--exe Client/Game.patched.exe]

A helper Python process maps the executable with LoadLibraryEx(DONT_RESOLVE_DLL_REFERENCES): its sections are
mapped and relocated, but no client code runs (no imports, entry point or TLS callbacks). Frida attaches to that
helper and runs resolveClient() in each mode (pinned, signature = pinned addresses skipped, xref = cross-references
preferred). Every mode must reproduce every pinned VA, global and offset, and agree with the offline resolver
scripts/ci/resolve_client.py on the same file. Prints one table per mode; exits 1 on any difference.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys

import frida

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).parent
HOLD = r"""
import ctypes, sys
from ctypes import wintypes
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.LoadLibraryExW.restype = wintypes.HMODULE
kernel.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
module = kernel.LoadLibraryExW(sys.argv[1], None, 0x1)  # DONT_RESOLVE_DLL_REFERENCES: map only, run nothing
print(hex(module or 0), ctypes.get_last_error(), flush=True)
sys.stdin.read()  # until the checker closes the pipe
"""
EXPORT = """
rpc.exports.resolve = (base, signatures, mode) => {
  const module = Process.findModuleByAddress(ptr(base));
  const result = resolveClient(module, signatures, mode);
  return JSON.parse(JSON.stringify(result, (key, value) => key === 'address' || key === 'start' || key === 'captures'
    ? undefined : value));
};
"""


def offline(exe, signatures, mode):
    sys.path.insert(0, str(ROOT / "scripts" / "ci"))
    import resolve_client
    image = resolve_client.Image.open(exe)
    return image.base, resolve_client.resolve(signatures, image, mode)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--exe", default=str(ROOT / "Client" / "Game.patched.exe"))
    parser.add_argument("--signatures", default=str(HERE / "client-signatures.json"))
    options = parser.parse_args()
    signatures = json.loads(Path(options.signatures).read_text(encoding="utf-8"))
    # The base interpreter: a venv's python.exe is a launcher whose child would hold the mapping.
    helper = subprocess.Popen([getattr(sys, "_base_executable", sys.executable), "-I", "-c", HOLD, options.exe],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    failures = 0
    try:
        handle = helper.stdout.readline().split()
        if not handle or int(handle[0], 16) == 0:
            raise RuntimeError(f"could not map {options.exe} (error {handle[1] if len(handle) > 1 else '?'})")
        session = frida.attach(helper.pid)
        source = (HERE / "client-resolver.js").read_text(encoding="utf-8") + EXPORT
        script = session.create_script(source, name="localexile-signature-check")
        script.load()
        for mode in ("pinned", "signature", "xref"):
            js = script.exports_sync.resolve(handle[0], signatures, mode)
            base, py = offline(options.exe, signatures, mode)
            print(f"== {mode}: {js['elapsedMs']} ms in-process")
            for name, found in js["symbols"].items():
                theirs = py["symbols"][name]
                offline_va = f"{base + theirs['rva']:#x}" if theirs["rva"] is not None else None
                problems = []
                if found["va"] != found["pinned"]:
                    problems.append(f"pinned {found['pinned']}")
                if found["va"] != offline_va or found["method"] != theirs["method"]:
                    problems.append(f"offline {offline_va} {theirs['method']}")
                failures += bool(problems)
                detail = f" ({found['detail']})" if found["detail"] else ""
                print(f"  {name:<20} {str(found['va']):>12} {found['method']}{detail}"
                      + (" MISMATCH " + "; ".join(problems) if problems else ""))
            for name, found in js["globals"].items():
                ok = found["va"] == found["pinned"]
                failures += not ok
                print(f"  global {name:<13} {str(found['va']):>12} {found['method']} ({found['detail']})"
                      + ("" if ok else f" MISMATCH pinned {found['pinned']}"))
            for name, found in js["offsets"].items():
                theirs = py["offsets"][name]
                ok = found["value"] == int(found["pinned"], 16) == theirs["value"] and found["method"] == theirs["method"]
                failures += not ok
                if not ok or found["method"] == "constant":
                    value = "-" if found["value"] is None else hex(found["value"])
                    print(f"  offset {name:<20} {value:>7} {found['method']} ({found['detail']})"
                          + ("" if ok else f" MISMATCH pinned {found['pinned']}, offline {theirs['value']}"))
            print(f"  other offsets: {sum(o['method'] != 'constant' for o in js['offsets'].values())} derived from code,"
                  f" equal to the pinned values")
        session.detach()
    finally:
        helper.stdin.close()
        helper.wait(timeout=10)
    print("OK: in-process and offline resolvers agree and reproduce every pinned value" if not failures
          else f"{failures} difference(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
