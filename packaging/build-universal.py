"""Build a real universal .app and compressed DMG on a Mac. All build files are retained."""
import argparse
import hashlib
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
NAME = "Magic Monkei HATS"
EXE = "MagicMonkeiHATS"

def run(*args):
    print("+", " ".join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), check=True)

def build(inputs, output):
    if os.uname().sysname != "Darwin":
        raise RuntimeError("A Mac is required for lipo, code signing and hdiutil.")
    output.mkdir(parents=True, exist_ok=False)
    stage = output / "disk-image"
    app = stage / (NAME + ".app")
    macos = app / "Contents/MacOS"
    resources = app / "Contents/Resources"
    frameworks = app / "Contents/Frameworks"
    macos.mkdir(parents=True)
    resources.mkdir()
    frameworks.mkdir()
    hosts = []
    for rid in ("osx-arm64", "osx-x64"):
        payload = output / "runtimes" / rid
        payload.mkdir(parents=True)
        with tarfile.open(inputs / (rid + ".tar.gz")) as archive:
            archive.extractall(payload, filter="data")
        target = resources / rid
        target.mkdir()
        native = frameworks / rid
        native.mkdir()
        for file in payload.iterdir():
            if file.name == "MagicMonkei.HATS" or file.suffix == ".pdb":
                continue
            if file.suffix == ".dylib":
                shutil.copy2(file, native / file.name)
                (target / file.name).symlink_to("../../Frameworks/" + rid + "/" + file.name)
            elif file.is_dir():
                shutil.copytree(file, target / file.name)
            else:
                shutil.copy2(file, target / file.name)
        host = output / ("launcher-" + rid)
        run("dotnet", "run", "--project", ROOT / "packaging/HostWriter", "-c", "Release", "--",
            inputs / ("apphost-template-" + rid), host, "../Resources/" + rid + "/MagicMonkei.HATS.dll")
        hosts.append(host)
    run("/usr/bin/lipo", "-create", *hosts, "-output", macos / EXE)
    (macos / EXE).chmod(0o755)
    run("/usr/bin/lipo", macos / EXE, "-verify_arch", "arm64", "x86_64")
    shutil.copy2(ROOT / "packaging/AppIcon.icns", resources / "AppIcon.icns")
    shutil.copy2(ROOT / "THIRD-PARTY-NOTICES.txt", resources / "THIRD-PARTY-NOTICES.txt")
    shutil.copytree(ROOT / "packaging/Licenses", resources / "Licenses")
    with (app / "Contents/Info.plist").open("wb") as file:
        plistlib.dump({
            "CFBundleName": NAME,
            "CFBundleDisplayName": NAME,
            "CFBundleIdentifier": "com.magicmonkei.hats-installer",
            "CFBundleExecutable": EXE,
            "CFBundleIconFile": "AppIcon.icns",
            "CFBundlePackageType": "APPL",
            "CFBundleShortVersionString": "2.2.0",
            "CFBundleVersion": "2.2.0",
            "CFBundleSupportedPlatforms": ["MacOSX"],
            "LSMinimumSystemVersion": "14.0",
            "LSArchitecturePriority": ["arm64", "x86_64"],
            "NSHighResolutionCapable": True,
            "NSPrincipalClass": "NSApplication",
            "NSDocumentsFolderUsageDescription": "HATS saves verified recovery backups in your chosen Documents folder.",
            "NSRemovableVolumesUsageDescription": "HATS updates the SD card you select while preserving your games, ports and mods.",
        }, file)
    # Ad-hoc signing ensures valid Apple Silicon code signatures. It is not Developer ID signing or notarization.
    for dylib in sorted(frameworks.rglob("*.dylib")):
        run("/usr/bin/codesign", "--force", "--sign", "-", dylib)
    run("/usr/bin/codesign", "--force", "--sign", "-", "--entitlements", ROOT / "packaging/entitlements.plist", app)
    run("/usr/bin/codesign", "--verify", "--deep", "--strict", "--verbose=2", app)
    (stage / "Applications").symlink_to("/Applications")
    # Finder opens a small uncluttered drag-to-Applications window.
    try:
        from ds_store import DSStore
        with DSStore.open(str(stage / ".DS_Store"), "w+") as store:
            store["."]["bwsp"] = {"ShowStatusBar": False, "ShowToolbar": False, "ShowPathbar": False,
                "ShowSidebar": False, "WindowBounds": "{{180, 160}, {560, 330}}"}
            store["."]["icvp"] = {"viewOptionsVersion": 1, "backgroundType": 1,
                "backgroundColorRed": 0.08, "backgroundColorGreen": 0.05, "backgroundColorBlue": 0.12,
                "iconSize": 104.0, "gridSpacing": 100.0, "gridOffsetX": 0.0, "gridOffsetY": 0.0,
                "arrangeBy": "none", "textSize": 13.0, "labelOnBottom": True, "showItemInfo": False,
                "showIconPreview": False}
            store["."]["vSrn"] = ("long", 1)
            store[NAME + ".app"]["Iloc"] = (145, 140)
            store["Applications"]["Iloc"] = (410, 140)
    except ImportError:
        print("Finder layout helper unavailable; using standard Finder layout.")
    dmg = output / "Magic-Monkei-HATS-Universal.dmg"
    run("/usr/bin/hdiutil", "create", "-volname", NAME, "-srcfolder", stage, "-format", "UDZO", "-fs", "HFS+", dmg)
    run("/usr/bin/hdiutil", "verify", dmg)
    digest = hashlib.file_digest(dmg.open("rb"), "sha256").hexdigest()
    (output / "SHA256.txt").write_text(digest + "  " + dmg.name + "\n", encoding="utf-8")
    shutil.copy2(ROOT / "INSTALL.txt", output / "INSTALL.txt")
    print("Built", dmg)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--inputs", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    build(args.inputs.resolve(), args.output.resolve())
