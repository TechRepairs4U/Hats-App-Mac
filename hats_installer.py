"""
HATS SD Card Installer
----------------------
Editable reconstruction from the packaged HATS-Installer.app.

Downloads a HATS pack zip from the TechRepairs4U/HATS GitHub release,
extracts it to a temporary folder, optionally backs up the existing
atmosphere/bootloader/switch folders on an SD card, then replaces them
with the freshly downloaded ones.
"""

from __future__ import annotations

import ctypes
import os
import plistlib
import shutil
import ssl
import string
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import webbrowser
import zipfile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
import tkinter as tk


COPY_BUFFER_SIZE = 4 * 1024 * 1024
COPY_WORKERS = 6
RELEASE_TAG = "HATS-22.1.0"
RELEASE_BASE = f"https://github.com/TechRepairs4U/HATS/releases/download/{RELEASE_TAG}/"
ASSETS = {
    "HATS.MM.zip (Full HATS, no firmware update)": "HATS.MM.zip",
    "HATS.MM+FW.zip (Full HATS with firmware update)": "HATS.MM+FW.zip",
    "HATS.Minimalistic.zip (No themes)": "HATS.Minimalistic.zip",
}
TARGET_FOLDERS = ["atmosphere", "bootloader", "switch"]
MACOS_METADATA_FILES = {
    ".DS_Store",
    ".VolumeIcon.icns",
    ".apdisk",
}
MACOS_METADATA_DIRS = {
    ".DocumentRevisions-V100",
    ".Spotlight-V100",
    ".TemporaryItems",
    ".Trashes",
    ".fseventsd",
}


def _bootstrap_ssl_certs() -> None:
    try:
        import certifi

        os.environ.setdefault("SSL_CERT_FILE", certifi.where())
        os.environ.setdefault("REQUESTS_CA_BUNDLE", certifi.where())
        ssl._create_default_https_context = ssl.create_default_context
    except Exception:
        pass


def resource_path(name: str) -> Path:
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).parent))
    return base / name


def get_volume_label(path: str) -> str:
    if sys.platform.startswith("win"):
        root = path if path.endswith("\\") else f"{path}\\"
        try:
            label = ctypes.create_unicode_buffer(261)
            ok = ctypes.windll.kernel32.GetVolumeInformationW(
                ctypes.c_wchar_p(root),
                label,
                ctypes.sizeof(label),
                None,
                None,
                None,
                None,
                0,
            )
            if ok and label.value:
                return label.value
        except Exception:
            pass
        return Path(root).drive or path

    try:
        if sys.platform == "darwin":
            result = subprocess.run(
                ["diskutil", "info", "-plist", path],
                capture_output=True,
                check=False,
            )
            if result.returncode == 0:
                info = plistlib.loads(result.stdout)
                return info.get("VolumeName") or Path(path).name
        return Path(path).name
    except Exception:
        return Path(path).name or path


def get_removable_drives() -> list[str]:
    """Return removable drive roots on Windows, macOS, and Linux."""
    drives: list[str] = []

    if sys.platform.startswith("win"):
        try:
            bitmask = ctypes.windll.kernel32.GetLogicalDrives()
            for index, letter in enumerate(string.ascii_uppercase):
                if bitmask & (1 << index):
                    root = f"{letter}:\\"
                    if ctypes.windll.kernel32.GetDriveTypeW(root) == 2:
                        drives.append(root)
        except Exception:
            pass
        return drives

    if sys.platform == "darwin":
        volumes = Path("/Volumes")
        if volumes.exists():
            return sorted(str(p) for p in volumes.iterdir() if p.is_dir() and p.name != "Macintosh HD")
        return []

    for base in (Path("/media") / os.environ.get("USER", ""), Path("/run/media") / os.environ.get("USER", "")):
        if base.exists():
            drives.extend(str(p) for p in base.iterdir() if p.is_dir() and os.path.ismount(p))
    return sorted(set(drives))


def find_source_root(extract_dir: Path) -> Path:
    for folder in extract_dir.rglob("*"):
        if folder.is_dir():
            children = {child.name.lower() for child in folder.iterdir()}
            if any(target in children for target in TARGET_FOLDERS):
                return folder
    return extract_dir


def is_macos_metadata(path: Path) -> bool:
    name = path.name
    return name.startswith("._") or name in MACOS_METADATA_FILES or name in MACOS_METADATA_DIRS


def scrub_macos_metadata(root: Path) -> int:
    """Delete macOS Finder/AppleDouble metadata from a copied SD card tree."""
    removed = 0
    root = root.resolve()
    if not root.exists():
        return 0

    for current_root, dirnames, filenames in os.walk(root, topdown=True):
        current_path = Path(current_root)

        for dirname in list(dirnames):
            path = current_path / dirname
            if is_macos_metadata(path):
                shutil.rmtree(path, ignore_errors=True)
                dirnames.remove(dirname)
                removed += 1

        for filename in filenames:
            path = current_path / filename
            if is_macos_metadata(path):
                try:
                    path.unlink()
                    removed += 1
                except OSError:
                    pass

    return removed


def download_file(url: str, dest_path: Path, progress_cb=None) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "HATS-Installer"})
    with urllib.request.urlopen(request) as response, open(dest_path, "wb") as out:
        total = int(response.headers.get("Content-Length", "0") or 0)
        done = 0
        while True:
            chunk = response.read(COPY_BUFFER_SIZE)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if progress_cb and total:
                progress_cb(done / total * 100)


def _fast_copy_file(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    with open(src, "rb") as source, open(dst, "wb") as target:
        shutil.copyfileobj(source, target, COPY_BUFFER_SIZE)
    try:
        shutil.copystat(src, dst)
    except OSError:
        pass


def copy_tree(src: Path, dst: Path, progress_cb=None, merge: bool = False) -> None:
    if dst.exists() and not merge:
        shutil.rmtree(dst)
    dst.mkdir(parents=True, exist_ok=True)

    files: list[tuple[Path, Path]] = []
    for root, dirnames, filenames in os.walk(src):
        root_path = Path(root)
        dirnames[:] = [dirname for dirname in dirnames if not is_macos_metadata(root_path / dirname)]
        rel = root_path.relative_to(src)
        (dst / rel).mkdir(parents=True, exist_ok=True)
        for filename in filenames:
            if is_macos_metadata(root_path / filename):
                continue
            files.append((root_path / filename, dst / rel / filename))

    copied = 0
    lock = threading.Lock()

    def worker(item: tuple[Path, Path]) -> None:
        nonlocal copied
        _fast_copy_file(*item)
        with lock:
            copied += 1
            if progress_cb and files:
                progress_cb(copied / len(files) * 100)

    with ThreadPoolExecutor(max_workers=COPY_WORKERS) as pool:
        list(pool.map(worker, files))


def _is_windows_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def _relaunch_as_admin() -> bool:
    if not sys.platform.startswith("win"):
        return False
    params = " ".join(f'"{arg}"' for arg in sys.argv)
    try:
        rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, None, 1)
        return rc > 32
    except Exception:
        return False


def format_fat32_windows(drive_letter: str, total_size_bytes: int, log_cb=None, progress_cb=None) -> None:
    """Format a Windows volume as FAT32 using the built-in formatter.

    The packaged app used a low-level writer to support large SD cards. This
    editable version keeps the formatter conservative while you work on it.
    """
    if not _is_windows_admin():
        raise RuntimeError("Formatting requires administrator privileges.")
    drive = drive_letter.rstrip("\\")
    if not drive.endswith(":"):
        drive = drive[:2]
    cmd = ["format", drive, "/FS:FAT32", "/Q", "/V:HATS", "/Y"]
    if log_cb:
        log_cb("Running: " + " ".join(cmd))
    proc = subprocess.run(cmd, capture_output=True, text=True, shell=True)
    if proc.returncode != 0:
        raise RuntimeError((proc.stderr or proc.stdout or "format failed").strip())
    if progress_cb:
        progress_cb(100)


class Tooltip:
    def __init__(self, widget: tk.Widget, text: str):
        self.widget = widget
        self.text = text
        self.tip: tk.Toplevel | None = None
        widget.bind("<Enter>", self._show)
        widget.bind("<Leave>", self._hide)

    def _show(self, _event=None) -> None:
        if self.tip or not self.text:
            return
        x = self.widget.winfo_rootx() + 16
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 8
        self.tip = tk.Toplevel(self.widget)
        self.tip.wm_overrideredirect(True)
        self.tip.wm_geometry(f"+{x}+{y}")
        label = tk.Label(self.tip, text=self.text, bg="#111827", fg="white", padx=8, pady=5)
        label.pack()

    def _hide(self, _event=None) -> None:
        if self.tip:
            self.tip.destroy()
            self.tip = None


class App(tk.Tk):
    BG = "#f5f7fb"
    GREEN = "#16a34a"
    GREEN_HOVER = "#15803d"

    def __init__(self):
        super().__init__()
        self.title("Magic Monkei HATS Installer")
        self.geometry("760x680")
        self.minsize(680, 580)
        self.configure(bg=self.BG)

        self._set_icons()

        self.asset_var = tk.StringVar(value=next(iter(ASSETS)))
        self.drive_var = tk.StringVar(value="")
        self.drive_card_widgets: dict[str, tk.Frame] = {}
        self._last_drives: list[str] = []

        self._build_ui()
        self.refresh_drives()
        self.after(2500, self._auto_refresh_drives)

    def _set_icons(self) -> None:
        try:
            ico = resource_path("app_icon.ico")
            if ico.exists() and sys.platform.startswith("win"):
                self.iconbitmap(str(ico))
            png = resource_path("app_icon.png")
            if png.exists():
                self._icon_img = tk.PhotoImage(file=str(png))
                self.iconphoto(True, self._icon_img)
        except Exception:
            pass

    def _build_ui(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure("Accent.TButton", background=self.GREEN, foreground="white", padding=(14, 8))
        style.map("Accent.TButton", background=[("active", self.GREEN_HOVER)])

        header = tk.Frame(self, bg=self.BG)
        header.pack(fill="x", padx=24, pady=(20, 10))
        tk.Label(header, text="Magic Monkei HATS Installer", bg=self.BG, font=("Segoe UI", 20, "bold")).pack(anchor="w")
        tk.Label(
            header,
            text="Only use this tool with Hekate or a SD Card Reader ONLY!!!\n\nFor Hekate users please go to Tools > USB Tools > SD Card.\nFor SD Card Reader just use the tool direct.",
            bg=self.BG,
            fg="#374151",
            justify="left",
            wraplength=700,
        ).pack(anchor="w", pady=(8, 0))

        form = ttk.LabelFrame(self, text="1. Select Pack & SD Card")
        form.pack(fill="x", padx=24, pady=10)
        form.columnconfigure(1, weight=1)

        ttk.Label(form, text="HATS pack:").grid(row=0, column=0, sticky="w", padx=12, pady=12)
        self.asset_combo = ttk.Combobox(form, textvariable=self.asset_var, values=list(ASSETS), state="readonly")
        self.asset_combo.grid(row=0, column=1, sticky="ew", padx=12, pady=12)

        ttk.Label(form, text="SD card drive:").grid(row=1, column=0, sticky="nw", padx=12, pady=12)
        self.drive_cards_frame = tk.Frame(form, bg=self.BG)
        self.drive_cards_frame.grid(row=1, column=1, sticky="ew", padx=12, pady=12)

        controls = tk.Frame(self, bg=self.BG)
        controls.pack(fill="x", padx=24, pady=8)
        ttk.Button(controls, text="Refresh", command=self.refresh_drives).pack(side="left")
        self.format_btn = ttk.Button(controls, text="FAT32 Formatter", command=self.open_advanced_window)
        self.format_btn.pack(side="left", padx=8)
        self.run_btn = ttk.Button(controls, text="Install HATS", style="Accent.TButton", command=self.start)
        self.run_btn.pack(side="right")

        progress = tk.Frame(self, bg=self.BG)
        progress.pack(fill="x", padx=24, pady=(8, 0))
        self.progress = ttk.Progressbar(progress, mode="determinate", maximum=100)
        self.progress.pack(fill="x")
        self.progress_label = tk.Label(progress, text="Ready", bg=self.BG, fg="#374151")
        self.progress_label.pack(anchor="w", pady=(4, 0))

        log_frame = ttk.LabelFrame(self, text="Log")
        log_frame.pack(fill="both", expand=True, padx=24, pady=16)
        log_frame.rowconfigure(0, weight=1)
        log_frame.columnconfigure(0, weight=1)
        self.log = tk.Text(log_frame, height=10, wrap="word")
        self.log.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(log_frame, command=self.log.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.log.configure(yscrollcommand=scroll.set)

    def _auto_refresh_drives(self) -> None:
        drives = get_removable_drives()
        if drives != self._last_drives:
            self.refresh_drives(drives)
        self.after(2500, self._auto_refresh_drives)

    def refresh_drives(self, drives: list[str] | None = None) -> None:
        drives = get_removable_drives() if drives is None else drives
        self._last_drives = drives
        for child in self.drive_cards_frame.winfo_children():
            child.destroy()
        self.drive_card_widgets.clear()

        if not drives:
            tk.Label(self.drive_cards_frame, text="No external SD/USB drive found!", bg=self.BG, fg="#991b1b").pack(anchor="w")
            self.drive_var.set("")
            return

        for index, drive in enumerate(drives):
            card = self._build_drive_card(index, drive)
            card.grid(row=index // 2, column=index % 2, sticky="ew", padx=4, pady=4)
            self.drive_cards_frame.columnconfigure(index % 2, weight=1)
        if self.drive_var.get() not in drives:
            self._select_drive(drives[0])

    def _build_drive_card(self, index: int, drive_path: str) -> tk.Frame:
        card = tk.Frame(self.drive_cards_frame, bg="white", highlightthickness=1, highlightbackground="#d1d5db", padx=10, pady=8)
        label = get_volume_label(drive_path)
        tk.Label(card, text=label, bg="white", font=("Segoe UI", 10, "bold")).pack(anchor="w")
        tk.Label(card, text=drive_path, bg="white", fg="#6b7280").pack(anchor="w")
        card.bind("<Button-1>", lambda _e, d=drive_path: self._select_drive(d))
        for child in card.winfo_children():
            child.bind("<Button-1>", lambda _e, d=drive_path: self._select_drive(d))
        self.drive_card_widgets[drive_path] = card
        return card

    def _select_drive(self, drive: str) -> None:
        self.drive_var.set(drive)
        self._highlight_selected_drive()

    def _highlight_selected_drive(self) -> None:
        selected = self.drive_var.get()
        for drive, card in self.drive_card_widgets.items():
            card.configure(highlightbackground=self.GREEN if drive == selected else "#d1d5db", highlightthickness=2 if drive == selected else 1)

    def log_msg(self, msg: str) -> None:
        self.after(0, self._log_msg_main_thread, msg)

    def _log_msg_main_thread(self, msg: str) -> None:
        self.log.insert("end", f"[{datetime.now():%H:%M:%S}] {msg}\n")
        self.log.see("end")

    def set_progress(self, value: float, text: str | None = None) -> None:
        self.after(0, self._set_progress_main_thread, value, text)

    def _set_progress_main_thread(self, value: float, text: str | None = None) -> None:
        self.progress["value"] = max(0, min(100, value))
        if text:
            self.progress_label.configure(text=text)

    def _set_run_btn_state(self, state: str) -> None:
        self.run_btn.configure(state=state)
        self.format_btn.configure(state=state)

    def start(self) -> None:
        drive = self.drive_var.get()
        if not drive:
            messagebox.showerror("No SD card", "Please select an SD card drive first.")
            return
        self._set_run_btn_state("disabled")
        threading.Thread(target=self.run_job, args=(drive,), daemon=True).start()

    def run_job(self, drive: str) -> None:
        try:
            asset_name = ASSETS[self.asset_var.get()]
            url = RELEASE_BASE + asset_name
            with tempfile.TemporaryDirectory(prefix="hats_") as tmp:
                tmp_path = Path(tmp)
                zip_path = tmp_path / asset_name
                extract_dir = tmp_path / "extract"

                self.log_msg(f"Downloading {asset_name} ...")
                download_file(url, zip_path, lambda p: self.set_progress(p, "Downloading"))
                self.log_msg("Download complete.")

                self.set_progress(0, "Extracting")
                with zipfile.ZipFile(zip_path) as archive:
                    archive.extractall(extract_dir)
                source_root = find_source_root(extract_dir)

                sd_root = Path(drive)
                desktop = Path.home() / "Desktop"
                backup_root = desktop / "Switch Backup" / f"HATS_backup_{datetime.now():%Y%m%d_%H%M%S}"
                existing = [name for name in TARGET_FOLDERS if (sd_root / name).exists()]
                if existing:
                    self.log_msg("Backing up existing Switch folders...")
                    backup_root.mkdir(parents=True, exist_ok=True)
                    for name in existing:
                        copy_tree(sd_root / name, backup_root / name, lambda p, n=name: self.set_progress(p, f"Backing up {n}"), merge=False)
                    self.log_msg("Backup complete, saved to Desktop\\Switch Backup")

                bootloader_ini = None
                ini_src = sd_root / "bootloader" / "ini"
                if ini_src.exists():
                    self.log_msg("Saving bootloader/ini configs...")
                    bootloader_ini = tmp_path / "bootloader_ini"
                    copy_tree(ini_src, bootloader_ini, merge=False)

                for name in TARGET_FOLDERS:
                    src = source_root / name
                    if src.exists():
                        self.log_msg(f"Installing {name} on SD card...")
                        copy_tree(src, sd_root / name, lambda p, n=name: self.set_progress(p, f"Installing {n}"), merge=False)

                if bootloader_ini and bootloader_ini.exists():
                    self.log_msg("Restoring bootloader/ini configs...")
                    copy_tree(bootloader_ini, sd_root / "bootloader" / "ini", merge=True)
                    self.log_msg("bootloader/ini configs restored.")

                self.set_progress(0, "Scrubbing macOS metadata")
                self.log_msg("Scrubbing macOS metadata files from SD card...")
                removed = scrub_macos_metadata(sd_root)
                self.log_msg(f"Removed {removed} macOS metadata item(s).")

            self.set_progress(100, "Complete")
            self.after(0, messagebox.showinfo, "Success", "HATS pack installed to SD card successfully.")
        except Exception as exc:
            self.log_msg(f"Error: {exc}")
            self.after(0, messagebox.showerror, "Error", str(exc))
        finally:
            self.after(0, self._set_run_btn_state, "normal")

    def open_advanced_window(self) -> None:
        win = tk.Toplevel(self)
        win.title("FAT32 Formatter")
        win.geometry("560x320")
        win.transient(self)
        win.configure(bg=self.BG)
        tk.Label(win, text="FAT32 SD Card Formatter", bg=self.BG, font=("Segoe UI", 16, "bold")).pack(anchor="w", padx=20, pady=(18, 8))
        tk.Label(
            win,
            text="WARNING: Formatting will ERASE ALL DATA on the selected SD card - every file and folder will be permanently deleted and this cannot be undone.\n\nONLY DO THIS IF YOU HAVE A NEW SD CARD AS THIS WILL DELETE ALL GAMES AND SAVES FROM UR EMUMMC.\n\nOnly use this if you know what you are doing.",
            bg=self.BG,
            fg="#991b1b",
            justify="left",
            wraplength=520,
        ).pack(anchor="w", padx=20, pady=8)
        ttk.Button(win, text="Format SD Card (FAT32) - Erases Everything", command=self.format_drive).pack(anchor="e", padx=20, pady=16)

    def format_drive(self) -> None:
        drive = self.drive_var.get()
        if not drive:
            messagebox.showerror("No SD card", "Please select an SD card drive first.")
            return
        confirm = simpledialog.askstring(
            "Format SD Card - WARNING",
            f"Type FORMAT to erase {drive}\n\nALL DATA on this SD card will be permanently deleted - including anything currently on it (and anything you just installed). This action cannot be undone.\n\nDo you want to continue?",
        )
        if confirm != "FORMAT":
            return
        self._set_run_btn_state("disabled")
        threading.Thread(target=self._format_worker, args=(drive, 0), daemon=True).start()

    def _format_worker(self, drive: str, total_size_bytes: int) -> None:
        try:
            self.log_msg(f"Formatting {drive} as FAT32 ... please wait, do not remove the SD card.")
            if sys.platform.startswith("win"):
                format_fat32_windows(drive, total_size_bytes, self.log_msg, lambda p: self.set_progress(p, "Formatting"))
            elif sys.platform == "darwin":
                disk = self._get_macos_disk_identifier(drive)
                if not disk:
                    raise RuntimeError("Could not determine the SD card disk identifier.")
                proc = subprocess.run(["diskutil", "eraseDisk", "FAT32", "HATS", disk], capture_output=True, text=True)
                if proc.returncode != 0:
                    raise RuntimeError((proc.stderr or proc.stdout).strip())
            else:
                raise RuntimeError("Formatting is not supported on this platform.")
            self.set_progress(100, "Format complete")
            self.log_msg("Format complete - the SD card is now empty and formatted as FAT32.")
            self.after(0, self.refresh_drives)
            self.after(0, messagebox.showinfo, "Format Complete", f"{drive} has been formatted as FAT32.")
        except Exception as exc:
            self.log_msg(f"Format error: {exc}")
            self.after(0, messagebox.showerror, "Format Error", str(exc))
        finally:
            self.after(0, self._set_run_btn_state, "normal")

    def _get_macos_disk_identifier(self, mount_point: str) -> str | None:
        result = subprocess.run(["diskutil", "info", "-plist", mount_point], capture_output=True)
        if result.returncode != 0:
            return None
        info = plistlib.loads(result.stdout)
        parent = info.get("ParentWholeDisk") or info.get("DeviceIdentifier")
        return f"/dev/{parent}" if parent else None


if __name__ == "__main__":
    _bootstrap_ssl_certs()
    App().mainloop()
