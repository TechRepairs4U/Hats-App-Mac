"""Verify and launch the exact DMG on each native Mac runner; no SD card is changed."""
import argparse
import json
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("dmg", type=Path)
parser.add_argument("reports", type=Path)
args = parser.parse_args()
args.reports.mkdir(parents=True, exist_ok=True)
mount = args.reports.resolve() / "mounted-image"
mount.mkdir()
def run(*command):
    subprocess.run(list(map(str, command)), check=True, timeout=120)
run("/usr/bin/hdiutil", "verify", args.dmg)
run("/usr/bin/hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", mount, args.dmg)
try:
    app = mount / "Magic Monkei HATS.app"
    binary = app / "Contents/MacOS/MagicMonkeiHATS"
    run("/usr/bin/lipo", binary, "-verify_arch", "arm64", "x86_64")
    run("/usr/bin/codesign", "--verify", "--deep", "--strict", app)
    run(binary, "--native-smoke")
    report = args.reports.resolve() / "startup.json"
    run(binary, "--startup-check", report)
    result = json.loads(report.read_text())
    assert result["started"] and result["sd_accessed"] is False
    print("Packaged app launch passed:", result)
    host = args.reports.resolve() / "updater-host.app"
    run("/usr/bin/ditto", app, host)
finally:
    run("/usr/bin/hdiutil", "detach", mount)

# Run outside the mounted image, exactly as an installed app does. Exercise
# mounting, identity, signatures, staging and quarantine in an isolated copy.
run(host / "Contents/MacOS/MagicMonkeiHATS", "--validate-update", args.dmg.resolve(), args.reports.resolve() / "update-check")
