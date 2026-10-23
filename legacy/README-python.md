# Hats App Mac

This is an editable reconstruction from `HATS-Installer.dmg`.

## Run

```powershell
py -3.14 hats_installer.py
```

## Build

Windows:

```powershell
.\build.ps1
```

The built Windows executable will be created in `dist\HATS-Installer.exe`.

macOS:

```bash
python3 -m pip install -r requirements.txt
pyinstaller --noconfirm --windowed --name HATS-Installer \
  --icon app_icon.icns \
  --add-data "app_icon.png:." \
  --add-data "app_icon.icns:." \
  --add-data "sdcard_icon.png:." \
  --add-data "bmc_button.png:." \
  hats_installer.py
```

GitHub Actions builds macOS `.app` zip and `.dmg` artifacts automatically on every push and through manual workflow dispatch.

## Notes

- The original installer was a PyInstaller macOS app using Python 3.14 and Tk.
- The original top-level bytecode was recovered, but Python 3.14 decompilation support is limited, so this project is a practical editable reconstruction rather than byte-for-byte original source.
- The installer/download/copy/backup behavior and UI text were reconstructed from the app bytecode.
- The original app used a low-level FAT32 formatting routine on Windows. This dev version keeps the formatting path conservative and uses the Windows formatter command, so review that area before shipping formatter changes.
- The installer skips and scrubs macOS metadata files such as `.DS_Store`, `._*`, `.Spotlight-V100`, `.Trashes`, and `.fseventsd`.
