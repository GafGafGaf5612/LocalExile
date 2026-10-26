# usage: python -I scripts/re/levelgen/tourloop.py <tour file> [--account NAME] [--character NAME] [--stall SECONDS]
# Runs a Gateway capture tour (/tour, CaptureTour.cs) without anyone at the keyboard: starts the servers, the walk-map
# recorder and the client through scripts/dev/live.ps1 -Walkmaps, logs in (scripts/dev/client_control.py: the client's
# remembered login, else the LOCALEXILE_PASSWORD environment variable) and plays the character, which starts the tour or resumes
# the saved one (data/tour-left.txt). When the client dies, shows an exception or makes no progress for --stall
# seconds, the stop under way goes to data/walkability/tour-failures.txt (unless the Gateway already put it there), the
# client and the servers are restarted and the tour goes on. Ends when the tour is finished. Progress: logs/tourloop.log.
import argparse, datetime, json, os, re, subprocess, sys, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
STATE = ROOT / "data" / "tour-left.txt"
FAILURES = ROOT / "data" / "walkability" / "tour-failures.txt"
LOG = ROOT / "logs" / "tourloop.log"
GATEWAY_LOG = ROOT / "logs" / "live-gateway.log"
PYTHON = ROOT / ".venv" / "Scripts" / "python.exe"
CONTROL = ROOT / "scripts" / "dev" / "client_control.py"


def log(text):
    line = f"{datetime.datetime.now():%Y-%m-%dT%H:%M:%S} {text}"
    print(line, flush=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(line + "\n")


def ps(command, timeout=600):
    return subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], cwd=ROOT, capture_output=True, text=True,
                          timeout=timeout)


def control(*args, stdin=None, timeout=120):
    python = str(PYTHON) if PYTHON.exists() else sys.executable
    r = subprocess.run([python, str(CONTROL), *args], cwd=ROOT, input=stdin, capture_output=True, text=True, timeout=timeout)
    out = (r.stdout or "").strip().splitlines()
    try:
        value = json.loads(out[-1]) if out else {"ok": False, "error": r.stderr.strip()}
        # client_control answers {"ok": …, "result": {…}}: the result's fields beside ok.
        if isinstance(value.get("result"), dict):
            value = {**value["result"], "ok": value.get("ok")}
        return value
    except json.JSONDecodeError:
        return {"ok": False, "error": (r.stdout + r.stderr).strip()[-400:]}


def client_running():
    return "Game.patched" in ps("Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName").stdout


def recorder_running():
    r = ps("Get-CimInstance Win32_Process -Filter \"Name like 'python%'\" | Where-Object { $_.CommandLine -like '*walkmaps.py*' } | "
           "Select-Object -ExpandProperty ProcessId")
    return r.stdout.strip() != ""


def gateway_listening():
    return ps("@(Get-NetTCPConnection -LocalPort 6112 -State Listen -ErrorAction SilentlyContinue).Count").stdout.strip() not in ("", "0")


def stop_all():
    ps("Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue | Stop-Process -Force; "
       "foreach ($c in Get-NetTCPConnection -LocalPort 6112 -State Listen -ErrorAction SilentlyContinue) { "
       "$p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue; if ($p.ProcessName -eq 'dotnet') { Stop-Process -Id $p.Id -Force } }")
    time.sleep(2)


def state():
    """(total, captured, missed, current or None, left) from the Gateway's saved tour, or None without one."""
    try:
        lines = STATE.read_text(encoding="utf-8").splitlines()
    except OSError:
        return None
    def count(key):
        for l in lines:
            m = re.match(rf"^# {key}: (\d+)$", l)
            if m:
                return int(m.group(1))
        return 0
    current = next((l[len("# current: "):].strip() for l in lines if l.startswith("# current: ")), None)
    left = sum(1 for l in lines if l.strip() and not l.startswith("#"))
    return count("total"), count("captured"), count("missed"), current, left


def drop_current(reason):
    """The Gateway is down: the stop under way leaves the saved tour and goes to the failures."""
    try:
        lines = STATE.read_text(encoding="utf-8").splitlines()
    except OSError:
        return
    current = next((l[len("# current: "):].strip() for l in lines if l.startswith("# current: ")), None)
    if current is None:
        return
    out, dropped = [], False
    for l in lines:
        if l.startswith("# current: "):
            continue
        m = re.match(r"^# missed: (\d+)$", l)
        if m:
            l = f"# missed: {int(m.group(1)) + 1}"
        elif not dropped and l.strip() == current:
            dropped = True
            continue
        out.append(l)
    STATE.write_text("\n".join(out) + "\n", encoding="utf-8")
    with FAILURES.open("a", encoding="utf-8") as f:
        f.write(f"{datetime.datetime.now():%Y-%m-%dT%H:%M:%S} {current}: the client did not arrive ({reason})\n")
    log(f"dropped {current}: {reason}")


def start(account, password, character, tour_file):
    stop_all()
    r = ps("& scripts/dev/live.ps1 -Walkmaps -NoBuild -NoExtract", timeout=300)
    if r.returncode != 0:
        log(f"live.ps1 failed: {(r.stdout + r.stderr).strip()[-300:]}")
        return False
    # live.ps1 launched the client: wait for its login screen.
    deadline = time.monotonic() + 240
    while True:
        status = control("game:status", timeout=60)
        if status.get("screen") == "login":
            break
        if time.monotonic() > deadline:
            log(f"client did not reach the login screen: {str(status)[:200]}")
            return False
        time.sleep(3)
    # The client's remembered login when it has one, else the account and LOCALEXILE_PASSWORD.
    r = control("login:saved", timeout=120)
    if not r.get("ok") and password is not None:
        r = control(f"login:{account}", "--password-stdin", stdin=password + "\n", timeout=120)
    if not r.get("ok"):
        log(f"login failed: {str(r.get('error'))[:200]}")
        return False
    r = control(f"character:play:{character}", timeout=120)
    # With a saved tour the arrival sends the character on at once: the wait for it to stand in an area can time out.
    if not r.get("ok") and state() is None:
        log(f"play failed: {str(r.get('error'))[:200]}")
        return False
    if state() is None:
        r = control("command", f"/tour {tour_file}", timeout=60)
        log(f"tour started: {r.get('replies')}")
    else:
        log("client in the game; the saved tour resumes")
    return True


def main():
    p = argparse.ArgumentParser()
    p.add_argument("tour")
    p.add_argument("--account", default="devtester")
    p.add_argument("--character", default="DevWitch")
    p.add_argument("--stall", type=int, default=300)
    a = p.parse_args()
    password = os.environ.get("LOCALEXILE_PASSWORD")
    started_once = state() is not None
    failures = 0
    while True:
        s = state()
        if started_once and s is None:
            log("tour finished")
            return 0
        if not started_once or not (client_running() and gateway_listening() and recorder_running()):
            if s and s[3]:
                stop_all()
                drop_current("client, recorder or Gateway gone")
            log(f"(re)starting: {s}")
            if not start(a.account, password, a.character, a.tour):
                failures += 1
                if failures > 20:
                    log("too many failed starts; giving up")
                    return 1
                time.sleep(30)
                continue
            failures = 0
            started_once = True
        # Wait for progress: the count of captured + missed stops, or a new current stop.
        last = state()
        since = time.monotonic()
        while True:
            time.sleep(10)
            now = state()
            if now is None:
                break
            if not (client_running() and gateway_listening()):
                break
            if last is None or (now[1] + now[2], now[3]) != (last[1] + last[2], last[3]):
                last, since = now, time.monotonic()
                log(f"progress: {now[1]} captured, {now[2]} missed, {now[4]} left, now {now[3]}")
                continue
            # No stop under way: the client failed one (the Gateway listed it) and hangs on its error.
            if now[3] is None and time.monotonic() - since > 45:
                log(f"client failed a stop and waits; restarting")
                stop_all()
                break
            if time.monotonic() - since > a.stall:
                log(f"no progress for {a.stall} s at {now[3]}; restarting")
                stop_all()
                drop_current(f"no progress for {a.stall} s") if now[3] else None
                break


if __name__ == "__main__":
    sys.exit(main())
