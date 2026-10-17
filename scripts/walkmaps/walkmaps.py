"""Walk-map generator: captures the terrain walkability grid of every area your own client generates.

Usage (started for you by `scripts/dev/live.ps1 -Walkmaps`):
    python scripts/walkmaps/walkmaps.py [--process Game.patched.exe] [--out data/walkability]

Needs: pip install frida  (Python 3.9+). Attaches to the running client, waits for it if it is not running yet,
and writes data/walkability/<area>_seed<seed>.bin plus <area>_seed<seed>.json ({"w":..,"h":..}). The area and seed come
from data/walkability/current.json, which GameServer.Gateway writes on every connection. Existing files are kept.
Nothing is sent anywhere; the files stay on your machine (data/ is git-ignored).
"""

import argparse
import json
import os
import sys
import threading
import time

try:
    import frida
except ImportError:
    sys.exit("walkmaps: the frida package is missing. Install it with:  python -m pip install frida")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))


def log(text):
    print(f"[walkmaps] {text}", flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--process", default="Game.patched.exe", help="client process name")
    ap.add_argument("--out", default=os.path.join(REPO, "data", "walkability"), help="output folder")
    ap.add_argument("--wait", type=int, default=600, help="seconds to wait for the client to start")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    session = attach(args.process, args.wait)
    with open(os.path.join(HERE, "walkmaps.js"), encoding="utf-8") as f:
        script = session.create_script(f.read())

    done = threading.Event()
    script.on("message", lambda message, data: on_message(message, data, args.out))
    session.on("detached", lambda reason, *_: (log(f"client gone ({reason})"), done.set()))
    script.load()
    log(f"attached to {args.process}; writing to {args.out}")
    try:
        while not done.wait(1):
            pass
    except KeyboardInterrupt:
        pass


def attach(name, wait):
    deadline = time.time() + wait
    announced = False
    while True:
        try:
            return frida.attach(name)
        except frida.ProcessNotFoundError:
            if time.time() > deadline:
                sys.exit(f"walkmaps: {name} did not start within {wait} s")
            if not announced:
                log(f"waiting for {name} ...")
                announced = True
            time.sleep(1)
        except frida.PermissionDeniedError:
            sys.exit("walkmaps: permission denied attaching to the client (run both at the same privilege level)")


def on_message(message, data, out):
    if message.get("type") == "error":
        log(f"script error: {message.get('description') or message}")
        return
    payload = message.get("payload") or {}
    kind = payload.get("type")
    if kind == "ready":
        log(f"hook installed in {payload.get('module')}")
    elif kind == "refused":
        log(f"not hooking: {payload.get('reason')}")
    elif kind == "skip":
        log(f"ignored a grid with unexpected size {payload}")
    elif kind == "error":
        log(f"hook error: {payload.get('message')}")
    elif kind == "grid" and data is not None:
        save(out, payload["w"], payload["h"], data)


USED = set()  # current.json nonces that already got their grid
FRESH_SECONDS = 120


def save(out, w, h, cells):
    area = current_area(out)
    if area is None:
        log(f"got a {w}x{h} grid, but no data/walkability/current.json from the Gateway; not saved")
        return
    # One grid per area load: the first grid after the Gateway announced the area, and only while that is recent.
    if area.get("nonce") in USED or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        log(f"got a {w}x{h} grid that does not belong to a fresh area load; not saved")
        return
    USED.add(area.get("nonce"))
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    if os.path.exists(stem + ".bin"):
        log(f"{os.path.basename(stem)}.bin already exists; kept")
        return
    with open(stem + ".bin.tmp", "wb") as f:
        f.write(cells)
    os.replace(stem + ".bin.tmp", stem + ".bin")
    with open(stem + ".json", "w", encoding="utf-8") as f:
        json.dump({"w": w, "h": h}, f)
    log(f"saved {os.path.basename(stem)}.bin ({w}x{h})")


def current_area(out):
    path = os.path.join(out, "current.json")
    try:
        with open(path, encoding="utf-8") as f:
            area = json.load(f)
        return area if "id" in area and "seed" in area else None
    except (OSError, ValueError):
        return None


if __name__ == "__main__":
    main()
