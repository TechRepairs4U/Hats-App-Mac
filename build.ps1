$ErrorActionPreference = "Stop"
py -3.14 -m pip install -r requirements.txt
py -3.14 -m PyInstaller --noconfirm --onefile --windowed `
  --name HATS-Installer `
  --icon app_icon.ico `
  --add-data "app_icon.png;." `
  --add-data "app_icon.ico;." `
  --add-data "sdcard_icon.png;." `
  --add-data "bmc_button.png;." `
  hats_installer.py
