"""Check LaunchServices opening the published app, retaining launch errors."""
import argparse
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("dmg", type=Path)
parser.add_argument("reports", type=Path)
args = parser.parse_args()
reports = args.reports.resolve()
reports.mkdir(parents=True, exist_ok=True)
mount = reports / "mounted-image"
mount.mkdir()

def run(*command, check=True, timeout=60):
    result = subprocess.run(list(map(str, command)), capture_output=True, text=True, timeout=timeout)
    print("+", " ".join(map(str, command)), "exit:", result.returncode, flush=True)
    print(result.stdout, result.stderr, flush=True)
    if check:
        result.check_returncode()
    return result

def launch(app, name):
    report = reports / (name + ".json")
    result = run("/usr/bin/open", "-n", "-W", app, "--args", "--startup-check", report, check=False)
    ok = result.returncode == 0 and report.exists() and json.loads(report.read_text()).get("started")
    (reports / (name + "-result.json")).write_text(json.dumps({"success": bool(ok), "exit": result.returncode, "stdout": result.stdout, "stderr": result.stderr}))
    return ok

run("/usr/bin/sw_vers")
run("/usr/bin/hdiutil", "verify", args.dmg)
run("/usr/bin/hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", mount, args.dmg)
try:
    app = mount / "Magic Monkei HATS.app"
    run("/usr/bin/codesign", "--verify", "--deep", "--strict", app)
    run("/usr/bin/plutil", "-p", app / "Contents/Info.plist")
    run("/usr/bin/file", app / "Contents/MacOS/MagicMonkeiHATS")
    run("/bin/ls", "-l", app / "Contents/MacOS/MagicMonkeiHATS")
    mounted_ok = launch(app, "finder-mounted")
    installed = reports / "Installed/Magic Monkei HATS.app"
    installed.parent.mkdir()
    run("/usr/bin/ditto", app, installed)
    installed_ok = launch(installed, "finder-installed")
finally:
    run("/usr/bin/hdiutil", "detach", mount, check=False)
    logs = run("/usr/bin/log", "show", "--last", "5m", "--style", "compact", "--predicate", 'eventMessage CONTAINS[c] "MagicMonkei" OR eventMessage CONTAINS[c] "Magic Monkei"', check=False)
    (reports / "launch.log").write_text(logs.stdout + logs.stderr)
if not mounted_ok or not installed_ok:
    raise RuntimeError("Finder launch failed. See the retained launch results.")
